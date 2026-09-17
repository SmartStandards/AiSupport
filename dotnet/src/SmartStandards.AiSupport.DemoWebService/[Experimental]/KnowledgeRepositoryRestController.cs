using Logging.SmartStandards;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace AI.SmartStandards.KnowledgeAccess {

  /// <summary>
  /// Exposes one <see cref="IKnowledgeRepository"/> through a deliberately small,
  /// REST-like HTTP surface optimized for both human callers and simple AI agents.
  /// 
  /// The controller is intentionally self-describing from its entry URL:
  /// 
  /// - GET on a BeyondContent area returns a self-describing Markdown navigation response
  ///   containing logical child area paths and fully qualified absolute URLs.
  /// - GET on a ContentAggregation area returns the same self-describing navigation
  ///   preamble followed by the one-level aggregated Markdown content of that area.
  /// - GET on a ContentContainer area returns the complete aggregated Markdown content
  ///   exposed through that container.
  /// - POST appends Markdown content through <see cref="IKnowledgeRepository.TryAppendContent(string, string)"/>.
  /// - DELETE truncates the addressed content area through
  ///   <see cref="IKnowledgeRepository.TryTruncate(string)"/>.
  /// 
  /// More advanced repository operations such as rename, physical deletion, replacement
  /// and content movement are intentionally not exposed by this controller.
  /// </summary>
  [ApiController]
  [Route("api/knowledge")]
  [EndpointGroupName("REST-KnowledgeRepository")]
  public class KnowledgeRepositoryController : ControllerBase {

    private const string _MarkdownContentType = "text/markdown; charset=utf-8";

    private readonly IKnowledgeRepository _KnowledgeRepository;

    /// <summary>
    /// Creates the controller using the single knowledge repository supplied through
    /// constructor dependency injection.
    /// </summary>
    /// <param name="knowledgeRepository">
    /// The repository implementation exposed through this HTTP endpoint.
    /// </param>
    public KnowledgeRepositoryController(IKnowledgeRepository knowledgeRepository) {
      if (knowledgeRepository == null) {
        throw new ArgumentNullException(nameof(knowledgeRepository));
      }

      _KnowledgeRepository = knowledgeRepository;
    }

    /// <summary>
    /// Handles GET requests for the repository root.
    /// 
    /// The root request is forwarded to the same area-resolution logic used for arbitrary
    /// nested paths.
    /// </summary>
    /// <returns>
    /// A self-describing navigation listing for BeyondContent areas or aggregated
    /// Markdown for content-capable areas.
    /// </returns>
    [HttpGet]
    public IActionResult GetRoot() {
      return this.GetAreaInternal("/");
    }

    /// <summary>
    /// Handles GET requests for any nested logical knowledge area.
    /// 
    /// BeyondContent areas return a compact Markdown navigation document containing
    /// logical direct child paths and fully qualified direct child URLs.
    /// 
    /// ContentAggregation and ContentContainer areas return
    /// <see cref="IKnowledgeRepository.GetAggregatedContent(string)"/> as Markdown.
    /// </summary>
    /// <param name="area">The catch-all logical area path relative to the controller route.</param>
    /// <returns>The navigation listing or aggregated Markdown content.</returns>
    [HttpGet("{**area}")]
    public IActionResult GetArea(string area) {
      return this.GetAreaInternal(this.ToRepositoryArea(area));
    }

    /// <summary>
    /// Appends Markdown content to the repository root when the root is content-capable.
    /// 
    /// The request body is passed directly to
    /// <see cref="IKnowledgeRepository.TryAppendContent(string, string)"/>.
    /// </summary>
    /// <returns>204 on success or an appropriate error response.</returns>
    [HttpPost]
    [Consumes("text/markdown", "text/plain")]
    public IActionResult PostRoot() {
      return this.PostAreaInternal("/");
    }

    /// <summary>
    /// Appends Markdown content to one nested logical area.
    /// 
    /// On a ContentContainer, direct text may be appended and structured content is merged
    /// hierarchically.
    /// 
    /// On a ContentAggregation, the repository contract requires the payload to contain
    /// subordinate structure because the aggregation itself does not own direct content.
    /// 
    /// POST is rejected for BeyondContent areas or areas without append capability.
    /// </summary>
    /// <param name="area">The catch-all logical area path relative to the controller route.</param>
    /// <returns>204 on success or an appropriate error response.</returns>
    [HttpPost("{**area}")]
    [Consumes("text/markdown", "text/plain")]
    public IActionResult PostArea(string area) {
      return this.PostAreaInternal(this.ToRepositoryArea(area));
    }

    /// <summary>
    /// Truncates the repository root when the root is content-capable and truncation is
    /// supported.
    /// </summary>
    /// <returns>204 on success or an appropriate error response.</returns>
    [HttpDelete]
    public IActionResult DeleteRoot() {
      return this.DeleteAreaInternal("/");
    }

    /// <summary>
    /// Truncates one nested logical content area.
    /// 
    /// The addressed area itself is preserved. Its direct content and/or subordinate
    /// content tree are removed according to the repository contract.
    /// 
    /// DELETE is rejected for BeyondContent areas or areas without truncate capability.
    /// </summary>
    /// <param name="area">The catch-all logical area path relative to the controller route.</param>
    /// <returns>204 on success or an appropriate error response.</returns>
    [HttpDelete("{**area}")]
    public IActionResult DeleteArea(string area) {
      return this.DeleteAreaInternal(this.ToRepositoryArea(area));
    }

    /// <summary>
    /// Performs the polymorphic GET behavior defined by the area's content level.
    /// </summary>
    private IActionResult GetAreaInternal(string repositoryArea) {
      if (!this.TryGetAreaCapabilities(
            repositoryArea,
            out ContentLevel contentLevel,
            out bool supportsSubAreas,
            out bool canBeRenamed,
            out bool canBeDeleted,
            out bool canAddSubAreas,
            out bool canAppendContent,
            out bool canTruncate,
            out bool supportsResources
          )) {
        return this.NotFound(
          "Knowledge area not found: " + repositoryArea
        );
      }

      if (contentLevel == ContentLevel.BeyondContent) {
        string navigation = this.BuildNavigationResponse(
          repositoryArea,
          supportsSubAreas
        );

        return this.Content(
          navigation,
          _MarkdownContentType,
          Encoding.UTF8
        );
      }

      if (contentLevel == ContentLevel.ContentAggregation) {
        string response = this.BuildContentAggregationResponse(
          repositoryArea,
          supportsSubAreas
        );

        return this.Content(
          response,
          _MarkdownContentType,
          Encoding.UTF8
        );
      }

      string content = _KnowledgeRepository.GetAggregatedContent(
        repositoryArea
      );

      return this.Content(
        content,
        _MarkdownContentType,
        Encoding.UTF8
      );
    }

    /// <summary>
    /// Performs one append operation after validating that the target exists, is part of
    /// the content model and reports append capability.
    /// </summary>
    private IActionResult PostAreaInternal(string repositoryArea) {
      if (!this.TryGetAreaCapabilities(
            repositoryArea,
            out ContentLevel contentLevel,
            out bool supportsSubAreas,
            out bool canBeRenamed,
            out bool canBeDeleted,
            out bool canAddSubAreas,
            out bool canAppendContent,
            out bool canTruncate,
            out bool supportsResources
          )) {
        return this.NotFound(
          "Knowledge area not found: " + repositoryArea
        );
      }

      if (contentLevel == ContentLevel.BeyondContent) {
        return this.StatusCode(
          StatusCodes.Status405MethodNotAllowed,
          "POST is not available for navigation-only knowledge areas."
        );
      }

      if (!canAppendContent) {
        return this.StatusCode(
          StatusCodes.Status405MethodNotAllowed,
          "This knowledge area does not allow content append operations."
        );
      }

      string content;

      try {
        using StreamReader reader = new StreamReader(
          this.Request.Body,
          Encoding.UTF8,
          true,
          4096,
          true
        );

        content = reader.ReadToEnd();
      }
      catch (IOException ex) {
        DevLogger.LogError(ex);

        return this.BadRequest(
          "The request body could not be read."
        );
      }

      bool success = _KnowledgeRepository.TryAppendContent(
        repositoryArea,
        content
      );

      if (!success) {
        return this.Conflict(
          "The content could not be appended. The supplied structure may be invalid for this knowledge area or the repository state may have changed."
        );
      }

      return this.NoContent();
    }

    /// <summary>
    /// Performs one truncate operation after validating that the target exists, is part
    /// of the content model and reports truncate capability.
    /// </summary>
    private IActionResult DeleteAreaInternal(string repositoryArea) {
      if (!this.TryGetAreaCapabilities(
            repositoryArea,
            out ContentLevel contentLevel,
            out bool supportsSubAreas,
            out bool canBeRenamed,
            out bool canBeDeleted,
            out bool canAddSubAreas,
            out bool canAppendContent,
            out bool canTruncate,
            out bool supportsResources
          )) {
        return this.NotFound(
          "Knowledge area not found: " + repositoryArea
        );
      }

      if (contentLevel == ContentLevel.BeyondContent) {
        return this.StatusCode(
          StatusCodes.Status405MethodNotAllowed,
          "DELETE is not available for navigation-only knowledge areas."
        );
      }

      if (!canTruncate) {
        return this.StatusCode(
          StatusCodes.Status405MethodNotAllowed,
          "This knowledge area does not allow truncate operations."
        );
      }

      bool success = _KnowledgeRepository.TryTruncate(
        repositoryArea
      );

      if (!success) {
        return this.Conflict(
          "The knowledge area could not be truncated. The repository state may have changed or the operation could not be completed atomically."
        );
      }

      return this.NoContent();
    }

    /// <summary>
    /// Builds a compact self-describing navigation response for a BeyondContent area.
    /// 
    /// The response deliberately contains fully qualified URLs rather than only logical
    /// path fragments so a simple AI agent can discover the next accessible locations
    /// from a single entry URL without additional API documentation.
    /// 
    /// Only direct child areas are listed. A caller can follow any URL and repeat GET to
    /// continue traversing the repository.
    /// </summary>
    private string BuildNavigationResponse(
      string repositoryArea,
      bool supportsSubAreas
    ) {
      StringBuilder builder = new StringBuilder();

      builder.Append("Knowledge area: `");
      builder.Append(repositoryArea);
      builder.Append('`');
      builder.Append(Environment.NewLine);
      builder.Append(Environment.NewLine);

      if (!supportsSubAreas) {
        builder.Append("This knowledge area contains no directly accessible sub-areas.");
        return builder.ToString();
      }

      string[] childAreas = _KnowledgeRepository.GetAreas(
        false,
        repositoryArea
      );

      if (childAreas.Length == 0) {
        builder.Append("This knowledge area contains no directly accessible sub-areas.");
        return builder.ToString();
      }

      builder.Append("The following directly accessible sub-area URLs are available:");
      builder.Append(Environment.NewLine);
      builder.Append(Environment.NewLine);

      foreach (string childArea in childAreas) {
        builder.Append("- `");
        builder.Append(childArea);
        builder.Append("` -> <");
        builder.Append(this.BuildAbsoluteAreaUrl(childArea));
        builder.Append('>');
        builder.Append(Environment.NewLine);
      }

      builder.Append(Environment.NewLine);
      builder.Append("Use HTTP GET on any URL above to continue browsing or to retrieve its Markdown content.");

      return builder.ToString();
    }

    /// <summary>
    /// Builds the self-describing response for a ContentAggregation area.
    /// 
    /// Direct child URLs are rendered first so a simple client can continue navigating
    /// into subdirectories or address individual content containers directly. The
    /// repository's one-level aggregated Markdown content follows after a separator.
    /// 
    /// The repository implementation is responsible for ensuring that aggregation does
    /// not recursively absorb independent child navigation scopes.
    /// </summary>
    private string BuildContentAggregationResponse(
      string repositoryArea,
      bool supportsSubAreas
    ) {
      StringBuilder builder = new StringBuilder();

      string navigation = this.BuildNavigationResponse(
        repositoryArea,
        supportsSubAreas
      );

      if (!string.IsNullOrWhiteSpace(navigation)) {
        builder.Append(navigation.TrimEnd('\r', '\n'));
      }

      string content = _KnowledgeRepository.GetAggregatedContent(
        repositoryArea
      );

      if (!string.IsNullOrWhiteSpace(content)) {
        if (builder.Length > 0) {
          builder.Append(Environment.NewLine);
          builder.Append(Environment.NewLine);
          builder.Append("---");
          builder.Append(Environment.NewLine);
          builder.Append(Environment.NewLine);
        }

        builder.Append(content.TrimStart('\r', '\n'));
      }

      return builder.ToString();
    }

    /// <summary>
    /// Builds a fully qualified absolute HTTP URL for one logical repository area.
    /// 
    /// Each logical path segment is URI-escaped individually so area hierarchy remains
    /// visible while special characters inside a segment remain safe.
    /// </summary>
    private string BuildAbsoluteAreaUrl(string repositoryArea) {
      string path = this.BuildAreaRequestPath(repositoryArea);

      return this.Request.Scheme
        + "://"
        + this.Request.Host.Value
        + this.Request.PathBase.Value
        + path;
    }

    /// <summary>
    /// Builds the controller-relative HTTP request path for one logical repository area.
    /// </summary>
    private string BuildAreaRequestPath(string repositoryArea) {
      string controllerPath = "/api/knowledge";

      if (repositoryArea == "/") {
        return controllerPath;
      }

      string[] segments = repositoryArea
        .Split('/', StringSplitOptions.RemoveEmptyEntries);

      StringBuilder builder = new StringBuilder();
      builder.Append(controllerPath);

      foreach (string segment in segments) {
        builder.Append('/');
        builder.Append(Uri.EscapeDataString(segment));
      }

      return builder.ToString();
    }

    /// <summary>
    /// Converts the catch-all controller route value into one canonical repository area
    /// path.
    /// </summary>
    private string ToRepositoryArea(string area) {
      if (string.IsNullOrWhiteSpace(area)) {
        return "/";
      }

      string normalized = area.Replace('\\', '/').Trim('/');

      if (string.IsNullOrEmpty(normalized)) {
        return "/";
      }

      return "/" + normalized;
    }

    /// <summary>
    /// Resolves area capabilities and converts a provider-specific missing-area exception
    /// into a false result suitable for HTTP 404 handling.
    /// </summary>
    private bool TryGetAreaCapabilities(
      string area,
      out ContentLevel contentLevel,
      out bool supportsSubAreas,
      out bool canBeRenamed,
      out bool canBeDeleted,
      out bool canAddSubAreas,
      out bool canAppendContent,
      out bool canTruncate,
      out bool supportsResources
    ) {
      contentLevel = ContentLevel.BeyondContent;
      supportsSubAreas = false;
      canBeRenamed = false;
      canBeDeleted = false;
      canAddSubAreas = false;
      canAppendContent = false;
      canTruncate = false;
      supportsResources = false;

      try {
        _KnowledgeRepository.GetAreaCapabilities(
          area,
          out contentLevel,
          out supportsSubAreas,
          out canBeRenamed,
          out canBeDeleted,
          out canAddSubAreas,
          out canAppendContent,
          out canTruncate,
          out supportsResources
        );

        return true;
      }
      catch (InvalidOperationException) {
        return false;
      }
    }
  }
}

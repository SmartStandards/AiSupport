using Logging.SmartStandards.CopyForAI.SmartStandards;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AI.SmartStandards {

  internal sealed class McpToolInvoker {

    private readonly IServiceScopeFactory _ScopeFactory;

    public McpToolInvoker(IServiceScopeFactory scopeFactory) {
      _ScopeFactory = scopeFactory;
    }

    public async Task<McpToolCallResult> InvokeAsync(HttpContext outerContext, McpToolDefinition tool, JObject arguments) {
      McpDiagnosticTrace.Write(outerContext, 2097980883295868220L, MyLogEventKind.McpToolPreparing);
      try {
        using (IServiceScope scope = _ScopeFactory.CreateScope()) {
          DefaultHttpContext targetContext = this.CreateTargetContext(outerContext, scope.ServiceProvider, tool, arguments);
          RouteData routeData = this.CreateRouteData(tool, arguments);
          ActionContext actionContext = new ActionContext(targetContext, routeData, tool.ApiDescription.ActionDescriptor, new ModelStateDictionary());

          McpDiagnosticTrace.Write(outerContext, 2097980883296400211L, MyLogEventKind.McpTargetAuthorizationStarted);
          bool authorized = await this.AuthorizeAsync(scope.ServiceProvider, targetContext, tool.ApiDescription.ActionDescriptor).ConfigureAwait(false);
          McpDiagnosticTrace.Write(outerContext, 2097980883297159212L, MyLogEventKind.McpTargetAuthorizationCompleted,
            "authorized=" + authorized);
          if (!authorized) {
            JObject authorizationFailure = new JObject();
            authorizationFailure["statusCode"] = StatusCodes.Status403Forbidden;
            authorizationFailure["message"] = "Target action authorization failed.";
            return new McpToolCallResult(true, "Target action authorization failed.", authorizationFailure);
          }

          IActionInvokerFactory actionInvokerFactory = scope.ServiceProvider.GetRequiredService<IActionInvokerFactory>();
          IActionInvoker actionInvoker = actionInvokerFactory.CreateInvoker(actionContext);
          if (actionInvoker == null) {
            throw new InvalidOperationException("No ASP.NET Core action invoker could be created for MCP tool '" + tool.Name + "'.");
          }

          McpDiagnosticTrace.Write(outerContext, 2097980883297427665L, MyLogEventKind.McpTargetInvocationStarted);
          await actionInvoker.InvokeAsync().ConfigureAwait(false);
          McpDiagnosticTrace.Write(outerContext, 2097980883298070243L, MyLogEventKind.McpTargetInvocationCompleted,
            "targetStatus=" + targetContext.Response.StatusCode);
          McpDiagnosticTrace.Write(outerContext, 2097980883298778128L, MyLogEventKind.McpToolResultReading);
          McpToolCallResult result = await this.CreateResultAsync(targetContext).ConfigureAwait(false);
          McpDiagnosticTrace.Write(outerContext, 2097980883298954907L, MyLogEventKind.McpToolCompleted,
            "isError=" + result.IsError);
          return result;
        }
      }
      catch (McpAuthenticationRequiredException) {
        McpDiagnosticTrace.Write(outerContext, 2097980883299896246L, MyLogEventKind.McpToolFailure,
          "category=target-authentication; targetStatus=401");
        throw;
      }
      catch (Exception ex) {
        McpDiagnosticTrace.Write(outerContext, 2097980883300245784L, MyLogEventKind.McpToolFailure,
          "category=execution; exceptionType=" + UnwrapException(ex).GetType().Name);
        string message = CreateAgentMessage(UnwrapException(ex));
        JObject error = new JObject();
        error["message"] = message;
        return new McpToolCallResult(true, message, error);
      }
    }

    /// <summary>
    /// Removes reflection/task wrappers to get the exception thrown by the service implementation.
    /// </summary>
    private static Exception UnwrapException(Exception ex) {
      Exception current = ex;
      while ((current is TargetInvocationException || current is AggregateException) && current.InnerException != null) {
        current = current.InnerException;
      }
      return current;
    }

    /// <summary>
    /// Converts an exception into a message for the agent: business/validation exceptions keep their message
    /// (without .NET specific suffixes like "(Parameter 'x')"), all others are reduced to a neutral message.
    /// The exception itself is always logged.
    /// </summary>
    internal static string CreateAgentMessage(Exception ex) {
      DevLogger.LogError(ex);

      if (ex is ArgumentException) {
        return Regex.Replace(ex.Message, "\\s*\\(Parameter '[^']*'\\)\\s*$", string.Empty);
      }
      if (ex is OperationCanceledException) {
        return "The operation timed out or was canceled.";
      }
      if (ex is HttpRequestException) {
        return "A backend service could not be reached: " + ex.Message;
      }
      if (ex is InvalidOperationException || ex is NotSupportedException || ex is UnauthorizedAccessException || ex is KeyNotFoundException || ex is TimeoutException || ex is FormatException) {
        return ex.Message;
      }
      return "The operation failed because of an internal error of the service.";
    }

    private DefaultHttpContext CreateTargetContext(HttpContext outerContext, IServiceProvider serviceProvider, McpToolDefinition tool, JObject arguments) {
      DefaultHttpContext targetContext = new DefaultHttpContext();
      targetContext.RequestServices = serviceProvider;
      targetContext.User = outerContext.User;
      targetContext.TraceIdentifier = outerContext.TraceIdentifier;

      targetContext.Request.Method = tool.Registration.HttpMethod;
      targetContext.Request.Scheme = outerContext.Request.Scheme;
      targetContext.Request.Host = outerContext.Request.Host;
      targetContext.Request.PathBase = outerContext.Request.PathBase;
      targetContext.Request.Path = this.BuildConcretePath(tool, arguments);
      targetContext.Request.QueryString = this.BuildQueryString(tool, arguments);
      targetContext.Request.Headers["Accept"] = "application/json";

      if (outerContext.Request.Headers.ContainsKey("Authorization")) {
        targetContext.Request.Headers["Authorization"] = outerContext.Request.Headers["Authorization"];
      }

      this.WriteBody(targetContext, tool, arguments);
      targetContext.Response.Body = new MemoryStream();
      return targetContext;
    }

    private RouteData CreateRouteData(McpToolDefinition tool, JObject arguments) {
      RouteData routeData = new RouteData();

      foreach (KeyValuePair<string, string> routeValue in tool.ApiDescription.ActionDescriptor.RouteValues) {
        routeData.Values[routeValue.Key] = routeValue.Value;
      }

      foreach (ApiParameterDescription parameter in tool.ApiDescription.ParameterDescriptions) {
        if (!this.IsPathParameter(parameter)) {
          continue;
        }

        JToken value = arguments[parameter.Name];
        if (value != null) {
          routeData.Values[parameter.Name] = this.ConvertTokenToString(value);
        }
      }

      return routeData;
    }

    private async Task<bool> AuthorizeAsync(IServiceProvider serviceProvider, HttpContext targetContext, ActionDescriptor actionDescriptor) {
      object[] endpointMetadata = actionDescriptor.EndpointMetadata.ToArray();
      bool allowAnonymous = endpointMetadata.OfType<IAllowAnonymous>().Any();
      if (allowAnonymous) {
        return true;
      }

      IAuthorizeData[] authorizeData = endpointMetadata.OfType<IAuthorizeData>().ToArray();
      if (authorizeData.Length == 0) {
        return true;
      }

      IAuthorizationPolicyProvider policyProvider = serviceProvider.GetRequiredService<IAuthorizationPolicyProvider>();
      AuthorizationPolicy policy = await AuthorizationPolicy.CombineAsync(policyProvider, authorizeData).ConfigureAwait(false);
      if (policy == null) {
        return true;
      }

      IAuthorizationService authorizationService = serviceProvider.GetRequiredService<IAuthorizationService>();
      AuthorizationResult result = await authorizationService.AuthorizeAsync(targetContext.User, targetContext, policy).ConfigureAwait(false);
      return result.Succeeded;
    }

    private PathString BuildConcretePath(McpToolDefinition tool, JObject arguments) {
      string relativePath = tool.Registration.RelativePath;
      foreach (ApiParameterDescription parameter in tool.ApiDescription.ParameterDescriptions) {
        if (!this.IsPathParameter(parameter)) {
          continue;
        }

        JToken value = arguments[parameter.Name];
        if (value == null) {
          continue;
        }

        string replacement = Uri.EscapeDataString(this.ConvertTokenToString(value));
        relativePath = Regex.Replace(relativePath, "\\{" + Regex.Escape(parameter.Name) + "(:[^}]*)?\\}", replacement, RegexOptions.IgnoreCase);
      }

      if (!relativePath.StartsWith("/", StringComparison.Ordinal)) {
        relativePath = "/" + relativePath;
      }
      return new PathString(relativePath);
    }

    private QueryString BuildQueryString(McpToolDefinition tool, JObject arguments) {
      List<string> pairs = new List<string>();
      foreach (ApiParameterDescription parameter in tool.ApiDescription.ParameterDescriptions) {
        if (!this.IsQueryParameter(parameter)) {
          continue;
        }

        JToken value = arguments[parameter.Name];
        if (value == null || value.Type == JTokenType.Null) {
          continue;
        }

        this.AppendQueryPairs(pairs, parameter.Name, value);
      }

      if (pairs.Count == 0) {
        return QueryString.Empty;
      }

      return new QueryString("?" + string.Join("&", pairs.ToArray()));
    }

    private void AppendQueryPairs(List<string> pairs, string name, JToken value) {
      JArray array = value as JArray;
      if (array != null) {
        foreach (JToken item in array) {
          this.AppendQueryPairs(pairs, name, item);
        }
        return;
      }

      string encodedName = Uri.EscapeDataString(name);
      string encodedValue = Uri.EscapeDataString(this.ConvertTokenToString(value));
      pairs.Add(encodedName + "=" + encodedValue);
    }

    private void WriteBody(DefaultHttpContext targetContext, McpToolDefinition tool, JObject arguments) {
      JToken body = null;
      if (tool.FlattenedBodyParameter != null) {
        body = arguments;
      }
      foreach (ApiParameterDescription parameter in tool.ApiDescription.ParameterDescriptions) {
        if (tool.FlattenedBodyParameter != null) {
          break;
        }
        if (!this.IsBodyParameter(parameter)) {
          continue;
        }
        if (body != null) {
          throw new NotSupportedException("MCP facade calls currently support at most one JSON body parameter.");
        }
        body = arguments[parameter.Name];
      }

      if (body == null) {
        targetContext.Request.Body = new MemoryStream();
        targetContext.Request.ContentLength = 0;
        return;
      }

      string bodyJson = body.ToString(Formatting.None);
      byte[] bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
      targetContext.Request.Body = new MemoryStream(bodyBytes);
      targetContext.Request.ContentType = "application/json";
      targetContext.Request.ContentLength = bodyBytes.Length;
    }

    private async Task<McpToolCallResult> CreateResultAsync(DefaultHttpContext targetContext) {
      targetContext.Response.Body.Position = 0;
      string responseBody;
      using (StreamReader reader = new StreamReader(targetContext.Response.Body, Encoding.UTF8, true, 1024, true)) {
        responseBody = await reader.ReadToEndAsync().ConfigureAwait(false);
      }

      JToken structuredContent = this.ParseStructuredContent(targetContext.Response.StatusCode, responseBody);
      bool isError = targetContext.Response.StatusCode >= 400;
      string text = responseBody;
      if (string.IsNullOrWhiteSpace(text)) {
        text = "HTTP " + targetContext.Response.StatusCode;
      }
      if (targetContext.Response.StatusCode == StatusCodes.Status401Unauthorized) {
        //the target requires (re-)authentication: propagated as real HTTP 401 incl. challenge (OAuth discovery)
        List<KeyValuePair<string, string>> challengeHeaders = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in targetContext.Response.Headers) {
          if (string.Equals(header.Key, "WWW-Authenticate", StringComparison.OrdinalIgnoreCase)) {
            challengeHeaders.Add(new KeyValuePair<string, string>(header.Key, header.Value.ToString()));
          }
        }
        throw new McpAuthenticationRequiredException(challengeHeaders.ToArray());
      }
      if (isError) {
        string message = ExtractErrorMessage(structuredContent, responseBody, targetContext.Response.StatusCode);
        JObject error = new JObject();
        error["message"] = message;
        return new McpToolCallResult(true, message, error);
      }

      return new McpToolCallResult(isError, text, structuredContent);
    }

    /// <summary>
    /// Extracts a single agent-readable message from an error response (ProblemDetails, UJMW fault or plain text).
    /// </summary>
    private static string ExtractErrorMessage(JToken structuredContent, string responseBody, int statusCode) {
      string message = null;
      JObject errorObject = structuredContent as JObject;
      if (errorObject != null) {
        string[] candidateNames = new string[] { "fault", "message", "detail", "title", "error" };
        foreach (string candidateName in candidateNames) {
          JToken candidate = errorObject.GetValue(candidateName, StringComparison.OrdinalIgnoreCase);
          if (candidate != null && candidate.Type == JTokenType.String && !string.IsNullOrWhiteSpace(candidate.ToString())) {
            message = candidate.ToString();
            break;
          }
        }
      }
      else if (!string.IsNullOrWhiteSpace(responseBody) && responseBody.Length <= 2000) {
        message = responseBody.Trim();
      }

      if (string.IsNullOrWhiteSpace(message)) {
        if (statusCode == StatusCodes.Status401Unauthorized || statusCode == StatusCodes.Status403Forbidden) {
          return "Access to the operation was denied.";
        }
        if (statusCode == StatusCodes.Status404NotFound) {
          return "The requested resource was not found.";
        }
        if (statusCode >= 500) {
          return "The operation failed because of an internal error of the service.";
        }
        return "The operation was rejected (HTTP " + statusCode + ").";
      }

      // remove .NET noise like "System.ArgumentException: " prefixes and "(Parameter 'x')" suffixes
      message = Regex.Replace(message, "^(System\\.)?([A-Za-z0-9_]+\\.)*[A-Za-z0-9_]*Exception:\\s*", string.Empty);
      message = Regex.Replace(message, "\\s*\\(Parameter '[^']*'\\)", string.Empty);
      return message.Trim();
    }

    private JToken ParseStructuredContent(int statusCode, string responseBody) {
      if (!string.IsNullOrWhiteSpace(responseBody)) {
        try {
          return JToken.Parse(responseBody);
        }
        catch (JsonException) {
        }
      }

      JObject fallback = new JObject();
      fallback["statusCode"] = statusCode;
      fallback["body"] = responseBody;
      return fallback;
    }

    private bool IsPathParameter(ApiParameterDescription parameter) {
      if (parameter.Source == null) {
        return false;
      }
      return parameter.Source.CanAcceptDataFrom(BindingSource.Path);
    }

    private bool IsQueryParameter(ApiParameterDescription parameter) {
      if (parameter.Source == null) {
        return false;
      }
      return parameter.Source.CanAcceptDataFrom(BindingSource.Query);
    }

    private bool IsBodyParameter(ApiParameterDescription parameter) {
      if (parameter.Source == null) {
        return false;
      }
      return parameter.Source.CanAcceptDataFrom(BindingSource.Body);
    }

    private string ConvertTokenToString(JToken value) {
      JValue simpleValue = value as JValue;
      if (simpleValue != null) {
        if (simpleValue.Value == null) {
          return string.Empty;
        }
        return Convert.ToString(simpleValue.Value, System.Globalization.CultureInfo.InvariantCulture);
      }
      return value.ToString(Formatting.None);
    }

  }

}

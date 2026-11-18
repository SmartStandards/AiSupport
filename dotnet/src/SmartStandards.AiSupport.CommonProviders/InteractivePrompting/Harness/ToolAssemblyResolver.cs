using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// TDL-2: process-wide resolver for the dependencies of tool DLLs which are referenced in place. It keeps the set of
  /// the original directories of all tool DLLs and probes them within 'AssemblyLoadContext.Resolving' (of every tool load
  /// context) and within 'AppDomain.CurrentDomain.AssemblyResolve'. Probing order: the preferred directory (directory of
  /// the tool DLL or of the requesting assembly), then all other registered directories. The first file with a matching
  /// simple name and a compatible (equal or higher) version wins.
  /// </summary>
  public static class ToolAssemblyResolver {

    private static readonly object _SyncRoot = new object();

    private static readonly List<string> _Directories = new List<string>();

    private static bool _AppDomainHandlerAttached = false;

    /// <summary>
    /// Returns a snapshot of the registered directories.
    /// </summary>
    public static string[] GetDirectories() {

      lock (_SyncRoot) {
        return _Directories.ToArray();
      }

    }

    /// <summary>
    /// Registers the directory of a tool DLL as probing directory and attaches the AppDomain handler (once).
    /// </summary>
    public static void RegisterDirectory(string directory) {

      if (string.IsNullOrWhiteSpace(directory)) {
        return;
      }

      string normalized = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

      lock (_SyncRoot) {

        if (!_Directories.Contains(normalized, StringComparer.OrdinalIgnoreCase)) {
          _Directories.Add(normalized);
        }

        if (!_AppDomainHandlerAttached) {
          AppDomain.CurrentDomain.AssemblyResolve += OnAppDomainAssemblyResolve;
          _AppDomainHandlerAttached = true;
        }

      }

    }

    /// <summary>
    /// Attaches the resolver to the 'Resolving' event of a tool load context.
    /// </summary>
    public static void Attach(AssemblyLoadContext loadContext, string toolAssemblyDirectory) {

      if (loadContext == null) {
        throw new ArgumentNullException(nameof(loadContext));
      }

      RegisterDirectory(toolAssemblyDirectory);

      loadContext.Resolving += (alc, assemblyName) => {

        string candidate = FindCandidate(assemblyName, toolAssemblyDirectory);

        if (candidate == null) {
          return null;
        }

        return alc.LoadFromAssemblyPath(candidate);
      };

    }

    /// <summary>
    /// Returns the full file name of the best matching assembly within the probing directories or null.
    /// </summary>
    public static string FindCandidate(AssemblyName assemblyName, string preferredDirectory) {

      if (assemblyName == null || string.IsNullOrWhiteSpace(assemblyName.Name)) {
        return null;
      }

      List<string> probingOrder = new List<string>();

      if (!string.IsNullOrWhiteSpace(preferredDirectory)) {
        probingOrder.Add(preferredDirectory);
      }

      foreach (string directory in GetDirectories()) {

        if (!probingOrder.Contains(directory, StringComparer.OrdinalIgnoreCase)) {
          probingOrder.Add(directory);
        }

      }

      foreach (string directory in probingOrder) {

        string candidate = Path.Combine(directory, assemblyName.Name + ".dll");

        if (File.Exists(candidate) && IsCompatible(candidate, assemblyName)) {
          return candidate;
        }

      }

      return null;
    }

    private static bool IsCompatible(string candidateFileFullName, AssemblyName requested) {

      if (requested.Version == null) {
        return true;
      }

      try {

        AssemblyName candidateName = AssemblyName.GetAssemblyName(candidateFileFullName);

        if (candidateName.Version == null) {
          return true;
        }

        return candidateName.Version >= requested.Version;
      }
      catch (BadImageFormatException) {
        return false;
      }

    }

    private static Assembly OnAppDomainAssemblyResolve(object sender, ResolveEventArgs args) {

      string preferredDirectory = null;

      if (args.RequestingAssembly != null && !string.IsNullOrEmpty(args.RequestingAssembly.Location)) {
        preferredDirectory = Path.GetDirectoryName(args.RequestingAssembly.Location);
      }

      string candidate = FindCandidate(new AssemblyName(args.Name), preferredDirectory);

      if (candidate == null) {
        return null;
      }

      return Assembly.LoadFrom(candidate);
    }

  }

}

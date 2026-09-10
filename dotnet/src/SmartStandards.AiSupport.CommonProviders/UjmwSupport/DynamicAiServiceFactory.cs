using AI.SmartStandards.LowLevelPrompting;
using Logging.SmartStandards.CopyForAI.SmartStandards;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Newtonsoft.Json;
using System.Reflection;
using System.Text;
using System.Web.UJMW;

namespace AI.SmartStandards.UjmwSupport {

  public static class DynamicAiServiceFactory {

    public static ICommonLLM AiOperationsProvider { get; set; } = null;

    public static TContract CreateInstance<TContract>(
      ImplementationMode implementationMode = ImplementationMode.PureAiCommunication
    ) {

      if(AiOperationsProvider == null) {
        throw new InvalidOperationException("AiOperationsProvider must be set before calling CreateInstance.");
      }

      IAbstractCallInvoker invoker = new UjmwToAiPipeline(
        AiOperationsProvider, typeof(TContract), implementationMode
      );

      return DynamicClientFactory.CreateInstance<TContract>(invoker);
    }

  }

  public enum ImplementationMode {
    PureAiCommunication = 0,
    AiGeneratedInmemoryCode = 1,
    AiGeneratedPermanentAssembly = 2,
    AiGeneratedPermanentAssemblyAndDocs = 3,
  }

  internal class UjmwToAiPipeline : IAbstractCallInvoker {

    //TODO: cahcing der assemly im dateisystem
    //TODO: assembly braucht sinnvolle version (die vom controact+lfd nr)
    //TODO: evtl doch die parameter einzeln üergeben oder gleich das komplette interface implementieren lassen...

    private ICommonLLM _AiOperationsProvider = null;
    private Type _ContractType = null;
    private ImplementationMode _ImplementationMode = ImplementationMode.PureAiCommunication;

    public UjmwToAiPipeline(
      ICommonLLM aiOperationsProvider, Type contractType, ImplementationMode implementationMode
    ) {

      _AiOperationsProvider = aiOperationsProvider;
      _ContractType = contractType;
      _ImplementationMode = implementationMode;

    }

    public object InvokeCall(
      string uniqueMethodNameOnTransportLayer, MethodInfo method,
      object[] arguments, string[] argumentNames, string methodSignatureString
    ) { 

      Action<int, string> logCallback = (logLevel, message) => {
        DevLogger.Log(logLevel, "DynamicCode", 0, 0, message);
      };

      if (_ImplementationMode == ImplementationMode.PureAiCommunication) {

        StringBuilder promptSb = new StringBuilder();
        promptSb.AppendLine($"The following method is called: {method.Name}.");

        string methodComment =  XmlCommentAccessExtensions.GetDocumentation(method, true);
        if (!string.IsNullOrWhiteSpace(methodComment)) {
          promptSb.AppendLine($"Its behavoiur is described as follows: {methodComment}"); 
        }

        int pIndex = 0;
        Dictionary<string,object> argumentDict = new Dictionary<string, object>();
        foreach (ParameterInfo param in method.GetParameters()) {

          promptSb.Append($"There is a input-parameter (index #{pIndex}) named '{param.Name}' ({param.ParameterType.FullName})");

          string pComment = XmlCommentAccessExtensions.GetDocumentation(param, true);
   
          if (!string.IsNullOrWhiteSpace(methodComment)) {
            promptSb.AppendLine($", described as: {methodComment}.");
          }
          else {
            promptSb.AppendLine(".");
          }

          argumentDict.Add(argumentNames[pIndex], arguments[pIndex]);
       
          pIndex++;
        }

        promptSb.AppendLine($"Please provide a valid response for this method call, in the expected return type!");
             
        if(method.ReturnType == typeof(void)) {
          promptSb.AppendLine($"Please emulate this 'call' to be a operation, executed within your context!");
          string prompt = promptSb.ToString();
          _AiOperationsProvider.CallWebSearchApi(
            prompt, argumentDict
          );
          return null;
        }
        else {
          promptSb.AppendLine($"Please emulate this 'call' to be a operation, executed within your context,");
          promptSb.AppendLine($"and provide a valid response, in the expected return type!,");
          string prompt = promptSb.ToString();
          var genMethod = typeof(ICommonLLM).GetMethods().Where(
            (m) => m.Name == nameof(ICommonLLM.CallWebSearchApi)  && m.ContainsGenericParameters
          ).First();

          object outcome = genMethod.MakeGenericMethod(method.ReturnType).Invoke(
            _AiOperationsProvider, new object[] { prompt, argumentDict }
          );

          return outcome;
        }
      }

      List<Assembly> assembliesToRefer = new List<Assembly>();
      assembliesToRefer.Add(Assembly.GetExecutingAssembly());
      assembliesToRefer.Add(typeof(JsonConvert).Assembly);
      assembliesToRefer.Add(typeof(IAbstractCallInvoker).Assembly);
      assembliesToRefer.Add(typeof(ICommonLLM).Assembly);

      foreach(AssemblyName assemblyName in _ContractType.Assembly.GetReferencedAssemblies()){
        try {
          assembliesToRefer.Add(Assembly.Load(assemblyName));
        }
        catch {
        }
      }

      ParameterInfo[] paramterInfos = method.GetParameters();
      StringBuilder inputInfo = new StringBuilder(2000);

      if (paramterInfos.Length == 0) {
        inputInfo.Append("which can be ignorred as a dummy");
      }
      else {
        inputInfo.Append($"which at any time must contain exactly the following items (representing IN/OUT-params): ");
        int ip = 0;
        foreach (ParameterInfo param in method.GetParameters()) {
          inputInfo.Append($"[{ip}] -> a value of {param.ParameterType.FullName} representing the argument '{param.Name}' ({param.GetDocumentation(true)}) */, ");
          ip++;
        }
      }

      string outputInfo;
      if (method.ReturnType == typeof(void)) {
        outputInfo = "has no return-type (void)";
      }
      else {
        outputInfo = "ALWAYS RETURNS A " + method.ReturnType.FullName;
        if (method.ReturnType.IsClass && !method.ReturnType.Namespace.StartsWith("System.")) {
          outputInfo = $"{outputInfo}, [a class, described as '{method.ReturnType.GetDocumentation(true)}') which has the following public properties: " +
            string.Join(", ", method.ReturnType.GetProperties(
              BindingFlags.Public | BindingFlags.Instance
            ).Select(p => $"{p.PropertyType.Name} {p.Name} /* {p.GetDocumentation(true)} */"));
        }
      }

      if (_ImplementationMode == ImplementationMode.AiGeneratedInmemoryCode) {

        string targetClassName = $"DynamicImpl__{_ContractType.Name}__{method.Name}";// __{Snowflake44.Generate()}";

        string methodDescription = method.GetDocumentation(true);
        string allowedLibs = ".NET-Core-Namespaces, "  + string.Join(", ", assembliesToRefer.Distinct().Select((a)=> a.GetName().Name));

        string codeGenPrompt =
          $"Generate C# code in form of a public static class '{targetClassName}':\n" +
          $" - it should be located within a namespace '{_ContractType.Namespace}'\n" +
          $" - prepend all needed using statements\n" +
          $" - the class contains a public static method named 'Execute'\n" +
          $" - the 'Execute'-Method has a first IN parameter named 'args' of type object[], {inputInfo}\n" +
          $" - the 'Execute'-Method has a second IN parameter named 'logCallback' of type Action<int,string>," +
          $" which should be used for logging during method-execution (int=log-level 0-Trace/2-Info/3-Warn/4-Error/5-Fatal; string=message)\n" +
          $" - the 'Execute'-Method {outputInfo}\n" +
          $" - all the code must not be related to external libraries but these: {allowedLibs}'\n" +
          $" - dont propagate the 'async-await'-pattern - terminate it early internally \n" +
          $" - awoid interations with the envrionment (like writing temp-files) - act like sandboxed unless the behaviour tells so.\n" +
          $" - dont generate additional public class members or additional parameters (use ONLY the specified inputs), generate internal helper-methods only as private\n" +
          $" - have a focus on stability and proper errorhandling, provide a max of information within exception-messages\n" +
          $" - generate high valued code-comments inside of the method\n" +
          $" - the method MUST BEHAVE EXACTLY LIKE described: {methodDescription}\n";

        //string aiCodeGenResponse = _AiOperationsProvider.CallWebSearchApi(codeGenPrompt);
        string aiCodeGenResponse = GetDummyCode();

        Assembly dynamicAssembly = CompileAssembly(
          aiCodeGenResponse,
          _ContractType.Namespace + "." + targetClassName,
          assembliesToRefer.Distinct().ToArray()
        );

        Type dynamicImplementedClass = dynamicAssembly.GetType(_ContractType.Namespace + "." + targetClassName);
        if (dynamicImplementedClass == null) {
          throw new InvalidOperationException("Type 'Bar.Foo' was not found.");
        }

        MethodInfo executeMethod = dynamicImplementedClass.GetMethod(
          "Execute", new Type[] { typeof(object[]) , typeof(Action<int,string>) }
        );
        if (executeMethod == null) {
          throw new InvalidOperationException("Static method 'Execute( object[], Action<int,string> )' was not found.");
        }

        object resultObject = executeMethod.Invoke(null, new object[] { arguments, logCallback });

        return resultObject;







        //should be a static class named 'AiGenerated.'
        // string codeToCompile = aiCodeGenResponse.GeneratedCode;

        //complie the c# code via Roslyn, load the assembly, and invoke the method to get the result:


      }





      throw new NotImplementedException("AiGeneratedInmemoryCode mode is not implemented yet.");

    }

    private static string GetDummyCode() {
      return
 @"
using Newtonsoft.Json;
using System;
namespace AI.KornSW {
    public sealed class DynamicImpl__IMeinTool__MultipliziereDieDifferenzZweiterZahlenMitSichSelbst {
        public static int Execute(object[] args, Action<int,string> logCallback) {
            return ((int)args[0] - (int)args[1]) * ((int)args[0] - (int)args[1]);
        }
    }
}";
    }

  /// <summary>
  /// Compiles C# source code into an in-memory assembly.
  /// </summary>
  /// <param name="sourceCode">The source code to compile.</param>
  /// <returns>The loaded assembly.</returns>
  private static Assembly CompileAssembly(string sourceCode, string assemblyName, params Assembly[] assembliesToRefer) {
      SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

      MetadataReference[] references = CreateMetadataReferences(assembliesToRefer);

      CSharpCompilation compilation = CSharpCompilation.Create(
          assemblyName,
          new SyntaxTree[] { syntaxTree },
          references,
          new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
      );

      using (MemoryStream memoryStream = new MemoryStream()) {
        EmitResult emitResult = compilation.Emit(memoryStream);

        if (!emitResult.Success) {
          Diagnostic[] errors = emitResult.Diagnostics
              .Where((diagnostic) => diagnostic.Severity == DiagnosticSeverity.Error)
              .ToArray();

          string errorText = string.Join(
              Environment.NewLine,
              errors.Select((diagnostic) => diagnostic.ToString())
          );

          throw new InvalidOperationException(
              "Dynamic compilation failed." + Environment.NewLine + errorText
          );
        }

        memoryStream.Position = 0;
        byte[] assemblyBytes = memoryStream.ToArray();
        Assembly assembly = Assembly.Load(assemblyBytes);

        return assembly;
      }
    }

    /// <summary>
    /// Builds the metadata reference set for .NET 8 dynamic compilation.
    /// </summary>
    /// <returns>Array of metadata references.</returns>
    private static MetadataReference[] CreateMetadataReferences(params Assembly[] assembliesToRefer) {
      string trustedAssembliesValue = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
      if (string.IsNullOrWhiteSpace(trustedAssembliesValue)) {
        throw new InvalidOperationException(
            "The runtime did not provide TRUSTED_PLATFORM_ASSEMBLIES."
        );
      }

      string[] trustedAssemblyPaths = trustedAssembliesValue.Split(Path.PathSeparator);

      List<MetadataReference> references = new List<MetadataReference>();

      foreach (string assemblyPath in trustedAssemblyPaths) {
        references.Add(MetadataReference.CreateFromFile(assemblyPath));
      }

      foreach (Assembly refAssembly in assembliesToRefer) {
        references.Add(MetadataReference.CreateFromFile(refAssembly.Location));
      }

      MetadataReference[] result = references
          .GroupBy((reference) => reference.Display, StringComparer.OrdinalIgnoreCase)
          .Select((group) => group.First())
          .ToArray();

      return result;
    }

  }

}

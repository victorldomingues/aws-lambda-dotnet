﻿using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Amazon.Lambda.Annotations.SourceGenerator.Diagnostics;
using Amazon.Lambda.Annotations.SourceGenerator.Extensions;
using Amazon.Lambda.Annotations.SourceGenerator.FileIO;
using Amazon.Lambda.Annotations.SourceGenerator.Models;
using Amazon.Lambda.Annotations.SourceGenerator.Models.Attributes;
using Amazon.Lambda.Annotations.SourceGenerator.Templates;
using Amazon.Lambda.Annotations.SourceGenerator.Writers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Amazon.Lambda.Annotations.SourceGenerator
{
    using System.Collections.Generic;

    [Generator]
    public class Generator : ISourceGenerator
    {
        private const string DEFAULT_LAMBDA_SERIALIZER = "Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer";
        private readonly IFileManager _fileManager = new FileManager();
        private readonly IDirectoryManager _directoryManager = new DirectoryManager();

        internal static readonly List<string> _allowdRuntimeValues = new List<string>(2)
        {
            "dotnet6",
            "provided.al2",
            "provided.al2023"
        };

        // Only allow alphanumeric characters
        private readonly Regex _resourceNameRegex = new Regex("^[a-zA-Z0-9]+$");

        // Regex for the 'Name' property for API Gateway attributes - https://docs.aws.amazon.com/apigateway/latest/developerguide/request-response-data-mappings.html
        private readonly Regex _parameterAttributeNameRegex = new Regex("^[a-zA-Z0-9._$-]+$");

        public Generator()
        {
#if DEBUG
            //if (!Debugger.IsAttached)
            //{
            //    Debugger.Launch();
            //}
#endif
        }

        public void Execute(GeneratorExecutionContext context)
        {
            var diagnosticReporter = new DiagnosticReporter(context);

            try
            {
                // retrieve the populated receiver
                if (!(context.SyntaxContextReceiver is SyntaxReceiver receiver))
                {
                    return;
                }

                // Check to see if any of the current syntax trees has any error diagnostics. If so
                // Skip generation. We only want to sync the CloudFormation template if the project
                // can compile.
                foreach(var syntaxTree in context.Compilation.SyntaxTrees)
                {
                    if(syntaxTree.GetDiagnostics().Any(x => x.Severity == DiagnosticSeverity.Error))
                    {
                        return;
                    }
                }


                // If no project directory was detected then skip the generator.
                // This is most likely to happen when the project is empty and doesn't have any classes in it yet.
                if(string.IsNullOrEmpty(receiver.ProjectDirectory))
                {
                    return;
                }

                var semanticModelProvider = new SemanticModelProvider(context);
                if (receiver.StartupClasses.Count > 1)
                {
                    foreach (var startup in receiver.StartupClasses)
                    {
                        // If there are more than one startup class, report them as errors
                        diagnosticReporter.Report(Diagnostic.Create(DiagnosticDescriptors.MultipleStartupNotAllowed,
                            Location.Create(startup.SyntaxTree, startup.Span),
                            startup.SyntaxTree.FilePath));
                    }
                }

                var isExecutable = false;

                bool foundFatalError = false;
                
                var assemblyAttributes = context.Compilation.Assembly.GetAttributes();
                
                var globalPropertiesAttribute = assemblyAttributes
                    .FirstOrDefault(attr => attr.AttributeClass.Name == nameof(LambdaGlobalPropertiesAttribute));

                var defaultRuntime = "dotnet6";

                if (globalPropertiesAttribute != null)
                {
                    var generateMain = globalPropertiesAttribute.NamedArguments.FirstOrDefault(kvp => kvp.Key == "GenerateMain").Value;
                    var runtimeAttributeValue = globalPropertiesAttribute.NamedArguments.FirstOrDefault(kvp => kvp.Key == "Runtime").Value;
                    var runtime = runtimeAttributeValue.Value == null ? defaultRuntime : runtimeAttributeValue.Value.ToString();

                    if (!_allowdRuntimeValues.Contains(runtime))
                    {
                        diagnosticReporter.Report(Diagnostic.Create(DiagnosticDescriptors.InvalidRuntimeSelection, Location.None));
                        return;
                    }

                    defaultRuntime = runtime;

                    isExecutable = generateMain.Value != null && bool.Parse(generateMain.Value.ToString());

                    if (isExecutable && context.Compilation.Options.OutputKind != OutputKind.ConsoleApplication)
                    {
                        diagnosticReporter.Report(Diagnostic.Create(DiagnosticDescriptors.SetOutputTypeExecutable, Location.None));
                        return;
                    }
                }

                var configureMethodModel = semanticModelProvider.GetConfigureMethodModel(receiver.StartupClasses.FirstOrDefault());

                var annotationReport = new AnnotationReport();

                var templateHandler = new CloudFormationTemplateHandler(_fileManager, _directoryManager);

                var lambdaModels = new List<LambdaFunctionModel>();
                
                foreach (var lambdaMethod in receiver.LambdaMethods)
                {
                    var lambdaMethodModel = semanticModelProvider.GetMethodSemanticModel(lambdaMethod);

                    if (!HasSerializerAttribute(context, lambdaMethodModel))
                    {
                        diagnosticReporter.Report(Diagnostic.Create(DiagnosticDescriptors.MissingLambdaSerializer,
                            lambdaMethod.GetLocation()));

                        foundFatalError = true;
                        continue;
                    }

                    // Check for necessary references
                    if (lambdaMethodModel.HasAttribute(context, TypeFullNames.RestApiAttribute)
                        || lambdaMethodModel.HasAttribute(context, TypeFullNames.HttpApiAttribute))
                    {
                        // Check for arbitrary type from "Amazon.Lambda.APIGatewayEvents"
                        if (context.Compilation.ReferencedAssemblyNames.FirstOrDefault(x => x.Name == "Amazon.Lambda.APIGatewayEvents") == null)
                        {
                            diagnosticReporter.Report(Diagnostic.Create(DiagnosticDescriptors.MissingDependencies,
                                lambdaMethod.GetLocation(),
                                "Amazon.Lambda.APIGatewayEvents"));

                            foundFatalError = true;
                            continue;
                        }
                    }
                    
                    var serializerInfo = GetSerializerInfoAttribute(context, lambdaMethodModel);

                    var model = LambdaFunctionModelBuilder.Build(lambdaMethodModel, configureMethodModel, context, isExecutable, serializerInfo, defaultRuntime);

                    // If there are more than one event, report them as errors
                    if (model.LambdaMethod.Events.Count > 1)
                    {
                        foreach (var attribute in lambdaMethodModel.GetAttributes().Where(attribute => TypeFullNames.Events.Contains(attribute.AttributeClass.ToDisplayString())))
                        {
                            diagnosticReporter.Report(Diagnostic.Create(DiagnosticDescriptors.MultipleEventsNotSupported,
                                Location.Create(attribute.ApplicationSyntaxReference.SyntaxTree, attribute.ApplicationSyntaxReference.Span),
                                DiagnosticSeverity.Error));
                        }

                        foundFatalError = true;
                        // Skip multi-event lambda method from processing and check remaining lambda methods for diagnostics
                        continue;
                    }
                    if(model.LambdaMethod.ReturnsIHttpResults && !model.LambdaMethod.Events.Contains(EventType.API))
                    {
                        diagnosticReporter.Report(Diagnostic.Create(DiagnosticDescriptors.HttpResultsOnNonApiFunction,
                            Location.Create(lambdaMethod.SyntaxTree, lambdaMethod.Span),
                            DiagnosticSeverity.Error));

                        foundFatalError = true;
                        continue;
                    }

                    if (!_resourceNameRegex.IsMatch(model.ResourceName))
                    {
                        diagnosticReporter.Report(Diagnostic.Create(DiagnosticDescriptors.InvalidResourceName,
                            Location.Create(lambdaMethod.SyntaxTree, lambdaMethod.Span),
                            DiagnosticSeverity.Error));

                        foundFatalError = true;
                        continue;
                    }

                    if (!AreLambdaMethodParamatersValid(lambdaMethod, model, diagnosticReporter))
                    {
                        foundFatalError = true;
                        continue;
                    }

                    var template = new LambdaFunctionTemplate(model);

                    string sourceText;
                    try
                    {
                        sourceText = template.TransformText().ToEnvironmentLineEndings();
                        context.AddSource($"{model.GeneratedMethod.ContainingType.Name}.g.cs", SourceText.From(sourceText, Encoding.UTF8, SourceHashAlgorithm.Sha256));
                    }
                    catch (Exception e) when (e is NotSupportedException || e is InvalidOperationException)  
                    {
                        diagnosticReporter.Report(Diagnostic.Create(DiagnosticDescriptors.CodeGenerationFailed, Location.Create(lambdaMethod.SyntaxTree, lambdaMethod.Span), e.Message));
                        return;
                    }

                    // report every generated file to build output
                    diagnosticReporter.Report(Diagnostic.Create(DiagnosticDescriptors.CodeGeneration, Location.None, $"{model.GeneratedMethod.ContainingType.Name}.g.cs", sourceText));

                    lambdaModels.Add(model);
                    annotationReport.LambdaFunctions.Add(model);
                }

                if (isExecutable)
                {
                    var executableAssembly = GenerateExecutableAssemblySource(
                        context,
                        diagnosticReporter,
                        receiver,
                        lambdaModels);

                    if (executableAssembly == null)
                    {
                        foundFatalError = true;
                        return;
                    }

                    context.AddSource("Program.g.cs", SourceText.From(executableAssembly.TransformText().ToEnvironmentLineEndings(), Encoding.UTF8, SourceHashAlgorithm.Sha256));
                }

                // Run the CloudFormation sync if any LambdaMethods exists. Also run if no LambdaMethods exists but there is a
                // CloudFormation template in case orphaned functions in the template need to be removed.
                // Both checks are required because if there is no template but there are LambdaMethods the CF template the template will be created.
                if (!foundFatalError && (receiver.LambdaMethods.Any() || templateHandler.DoesTemplateExist(receiver.ProjectDirectory)))
                {
                    annotationReport.CloudFormationTemplatePath = templateHandler.FindTemplate(receiver.ProjectDirectory);
                    annotationReport.ProjectRootDirectory = receiver.ProjectDirectory;
                    annotationReport.IsTelemetrySuppressed = ProjectFileHandler.IsTelemetrySuppressed(receiver.ProjectPath, _fileManager);

                    var templateFormat = templateHandler.DetermineTemplateFormat(annotationReport.CloudFormationTemplatePath);
                    ITemplateWriter templateWriter;
                    if (templateFormat == CloudFormationTemplateFormat.Json)
                    {
                        templateWriter = new JsonWriter();
                    }
                    else
                    {
                        templateWriter = new YamlWriter();
                    }
                    var cloudFormationWriter = new CloudFormationWriter(_fileManager, _directoryManager, templateWriter, diagnosticReporter);
                    cloudFormationWriter.ApplyReport(annotationReport);
                }

            }
            catch (Exception e)
            {
                // this is a generator failure, report this as error
                diagnosticReporter.Report(Diagnostic.Create(DiagnosticDescriptors.UnhandledException, Location.None, e.PrettyPrint()));
#if DEBUG
                throw;
#endif
            }
        }

        private static ExecutableAssembly GenerateExecutableAssemblySource(
            GeneratorExecutionContext context,
            DiagnosticReporter diagnosticReporter,
            SyntaxReceiver receiver,
            List<LambdaFunctionModel> lambdaModels)
        {
            // Check 'Amazon.Lambda.RuntimeSupport' is referenced
            if (context.Compilation.ReferencedAssemblyNames.FirstOrDefault(x => x.Name == "Amazon.Lambda.RuntimeSupport") == null)
            {
                diagnosticReporter.Report(
                    Diagnostic.Create(
                        DiagnosticDescriptors.MissingDependencies,
                        Location.None,
                        "Amazon.Lambda.RuntimeSupport"));
                
                return null;
            }

            foreach (var methodDeclaration in receiver.MethodDeclarations)
            {
                var model = context.Compilation.GetSemanticModel(methodDeclaration.SyntaxTree);
                var symbol = model.GetDeclaredSymbol(methodDeclaration) as IMethodSymbol;

                // Check to see if a static main method exists in the same namespace that has 0 or 1 parameters
                if (symbol.Name != "Main" || !symbol.IsStatic || symbol.ContainingNamespace.Name != lambdaModels[0].LambdaMethod.ContainingAssembly || (symbol.Parameters.Length > 1)) 
                    continue;
                
                diagnosticReporter.Report(
                    Diagnostic.Create(
                        DiagnosticDescriptors.MainMethodExists,
                        Location.None));
                    
                return null;
            }

            if (lambdaModels.Count == 0)
            {
                diagnosticReporter.Report(
                    Diagnostic.Create(
                        DiagnosticDescriptors.ExecutableWithNoFunctions,
                        Location.None,
                        "Amazon.Lambda.Annotations"));

                return null;
            }

            return new ExecutableAssembly(
                lambdaModels,
                lambdaModels[0].LambdaMethod.ContainingNamespace);
        }

        private bool HasSerializerAttribute(GeneratorExecutionContext context, IMethodSymbol methodModel)
        {
            return methodModel.ContainingAssembly.HasAttribute(context, TypeFullNames.LambdaSerializerAttribute);
        }

        private LambdaSerializerInfo GetSerializerInfoAttribute(GeneratorExecutionContext context, IMethodSymbol methodModel)
        {
            var serializerString = DEFAULT_LAMBDA_SERIALIZER;
            string serializerJsonContext = null;

            ISymbol symbol = null;
            
            // First check if method has the Lambda Serializer.
            if (methodModel.HasAttribute(
                    context,
                    TypeFullNames.LambdaSerializerAttribute))
            {
                symbol = methodModel;
            }
            // Then check assembly
            else if (methodModel.ContainingAssembly.HasAttribute(
                    context,
                    TypeFullNames.LambdaSerializerAttribute))
            {
                symbol = methodModel.ContainingAssembly;
            }
            // Else return the default serializer.
            else
            {
                return new LambdaSerializerInfo(serializerString, serializerJsonContext);
            }
            
            var attribute = symbol.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name == TypeFullNames.LambdaSerializerAttributeWithoutNamespace);
                
            var serializerValue = attribute.ConstructorArguments.FirstOrDefault(kvp => kvp.Type.Name == nameof(Type)).Value;

            if(serializerValue is INamedTypeSymbol typeSymbol && typeSymbol.Name.Contains("SourceGeneratorLambdaJsonSerializer") && typeSymbol.TypeArguments.Length == 1)
            {
                serializerJsonContext = typeSymbol.TypeArguments[0].ToString();
            }

            if (serializerValue != null)
            {
                serializerString = serializerValue.ToString();
            }

            return new LambdaSerializerInfo(serializerString, serializerJsonContext);
        }

        public void Initialize(GeneratorInitializationContext context)
        {
            // Register a syntax receiver that will be created for each generation pass
            context.RegisterForSyntaxNotifications(() => new SyntaxReceiver(_fileManager, _directoryManager));
        }

        private bool AreLambdaMethodParamatersValid(MethodDeclarationSyntax declarationSyntax, LambdaFunctionModel model, DiagnosticReporter diagnosticReporter)
        {
            var isValid = true;
            foreach (var parameter in model.LambdaMethod.Parameters)
            {
                if (parameter.Attributes.Any(att => att.Type.FullName == TypeFullNames.FromQueryAttribute))
                {
                    var fromQueryAttribute = parameter.Attributes.First(att => att.Type.FullName == TypeFullNames.FromQueryAttribute) as AttributeModel<APIGateway.FromQueryAttribute>;
                    // Use parameter name as key, if Name has not specified explicitly in the attribute definition.
                    var parameterKey = fromQueryAttribute?.Data?.Name ?? parameter.Name;

                    if (!parameter.Type.IsPrimitiveType() && !parameter.Type.IsPrimitiveEnumerableType())
                    {
                        isValid = false;
                        diagnosticReporter.Report(Diagnostic.Create(DiagnosticDescriptors.UnsupportedMethodParamaterType, 
                            Location.Create(declarationSyntax.SyntaxTree, declarationSyntax.Span),
                            parameterKey, parameter.Type.FullName));
                    }
                }

                foreach (var att in parameter.Attributes)
                {
                    var parameterAttributeName = string.Empty;
                    switch (att.Type.FullName)
                    {
                        case TypeFullNames.FromQueryAttribute:
                            var fromQueryAttribute = (AttributeModel<APIGateway.FromQueryAttribute>)att;
                            parameterAttributeName = fromQueryAttribute.Data.Name;
                            break;

                        case TypeFullNames.FromRouteAttribute:
                            var fromRouteAttribute = (AttributeModel<APIGateway.FromRouteAttribute>)att;
                            parameterAttributeName = fromRouteAttribute.Data.Name;
                            break;

                        case TypeFullNames.FromHeaderAttribute:
                            var fromHeaderAttribute = (AttributeModel<APIGateway.FromHeaderAttribute>)att;
                            parameterAttributeName = fromHeaderAttribute.Data.Name;
                            break;

                        default:
                            break;
                    }

                    if (!string.IsNullOrEmpty(parameterAttributeName) && !_parameterAttributeNameRegex.IsMatch(parameterAttributeName))
                    {
                        isValid = false;
                        diagnosticReporter.Report(Diagnostic.Create(DiagnosticDescriptors.InvalidParameterAttributeName,
                            Location.Create(declarationSyntax.SyntaxTree, declarationSyntax.Span),
                            parameterAttributeName, parameter.Name));
                    }
                }
            }

            return isValid;
        }
    }
}
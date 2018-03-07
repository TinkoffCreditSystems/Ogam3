﻿using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Ogam3.Network;
using Ogam3.Serialization;

namespace Ogam3.Lsp.Generators {
    class ClassRegistrator {
        public static void Register(EnviromentFrame env, object instanceOfImplementation) {
            const BindingFlags methodFlags =
                BindingFlags.Public | BindingFlags.FlattenHierarchy | BindingFlags.Instance;
            var type = instanceOfImplementation.GetType();

            var descriptors = new List<MethodDescriptors>();

            foreach (var interfaceType in type.GetInterfaces()
                .Where(t => t.GetCustomAttributes(typeof(EnviromentAttribute), false).Any())) {
                var envAtt = (EnviromentAttribute) interfaceType.GetCustomAttributes(typeof(EnviromentAttribute), true)
                    .FirstOrDefault();
                foreach (var interfaceMethodInfo in interfaceType.GetMethods(methodFlags)) {
                    var implMethod = type.GetMethod(interfaceMethodInfo.Name, methodFlags);

                    if (implMethod == null) continue;

                    descriptors.Add(new MethodDescriptors() {
                        MethodName = implMethod.Name,
                        DefineName =
                            String.IsNullOrWhiteSpace(envAtt.EnviromentName)
                                ? implMethod.Name
                                : $"{envAtt.EnviromentName}:{implMethod.Name}",
                        Arguments = implMethod.GetParameters(),
                        ReturnType = implMethod.ReturnType
                    });
                }

                var instantName = $"{type.Name}_ClassRegistrator";
                var nameSpaceName = "Ogam3.Lsp.Generators";
                var codeRoot = GenerateProxyClass(type, descriptors, nameSpaceName, instantName);
                
                //var code = CreateSrc(codeRoot); // TODO debug

                var compiler = CodeDomProvider.CreateProvider("CSharp");

                var DOMref =
                    AppDomain.CurrentDomain.GetAssemblies()
                        .Where(obj => !obj.IsDynamic)
                        .Select(obj => obj.Location)
                        .ToList();

                var currentAssembly = Assembly.GetExecutingAssembly();
                DOMref.Add(currentAssembly.Location);

                DOMref.Add(type.Assembly.Location);

                DOMref.AddRange(GetAssemblyFiles(type.Assembly));

                DOMref = DOMref.Distinct().ToList();

                var compilerParams = new CompilerParameters(DOMref.ToArray());
                compilerParams.GenerateInMemory = true;
                compilerParams.GenerateExecutable = false;
                compilerParams.IncludeDebugInformation = false;

                var cr = compiler.CompileAssemblyFromDom(compilerParams, codeRoot);

                foreach (var ce in cr.Errors) {
                    throw new Exception(ce.ToString());
                }

                var callerType = cr.CompiledAssembly.GetType($"{nameSpaceName}.{instantName}");

                Activator.CreateInstance(callerType, instanceOfImplementation, env);
            }
        }

        static IEnumerable<string> GetAssemblyFiles(Assembly assembly) {
            var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assemblyName in assembly.GetReferencedAssemblies())
                yield return loadedAssemblies.SingleOrDefault(a => a.FullName == assemblyName.FullName)?.Location;
        }

        private struct MethodDescriptors {
            public string MethodName;
            public string DefineName;
            public ParameterInfo[] Arguments;
            public Type ReturnType;
        }

        static string CreateSrc(CodeCompileUnit codeCompileUnit) {
            var provider = CodeDomProvider.CreateProvider("CSharp");
            using (var sourceWriter = new StringWriter()) {
                provider.GenerateCodeFromCompileUnit(codeCompileUnit, sourceWriter, new CodeGeneratorOptions());
                return sourceWriter.ToString();
            }
        }

        static CodeCompileUnit GenerateProxyClass(Type implementationType, List<MethodDescriptors> desptors,
            string nameSpaceName, string instantName) {
            var codeRoot = new CodeCompileUnit();

            var codeNamespace = new CodeNamespace(nameSpaceName);
            codeNamespace.Imports.Add(new CodeNamespaceImport("System"));
            codeNamespace.Imports.Add(new CodeNamespaceImport("System.Collections.Generic"));
            codeNamespace.Imports.Add(new CodeNamespaceImport("System.Collections"));

            foreach (var nmsp in GetTypeNamespaces(implementationType)) {
                codeNamespace.Imports.Add(new CodeNamespaceImport(nmsp));
            }

            codeRoot.Namespaces.Add(codeNamespace);

            var targetClass = new CodeTypeDeclaration(instantName);
            targetClass.IsClass = true;
            targetClass.TypeAttributes = TypeAttributes.Public | TypeAttributes.Sealed;

            targetClass.Members.Add(AddField(NameOfImplementationField, implementationType));
            targetClass.Members.Add(CreateCtor(implementationType, desptors));

            foreach (var mDescriptor in desptors) {
                targetClass.Members.Add(CreateMethod(mDescriptor.MethodName, mDescriptor.Arguments,
                    mDescriptor.ReturnType));
            }

            codeNamespace.Types.Add(targetClass);

            return codeRoot;
        }

        private static ICollection<string> GetTypeNamespaces(Type t) {
            var res = new List<string> {t.Namespace};

            if (t.IsGenericType) {
                Array.ForEach<Type>(t.GetGenericArguments(), tt => res.AddRange(GetTypeNamespaces(tt)));
            }

            foreach (var mb in t.GetMembers(BindingFlags.Instance | BindingFlags.Public)) {
                if (mb is FieldInfo) {
                    var f = (FieldInfo) mb;
                    if (IsBaseType(f.FieldType))
                        continue;
                    res.AddRange(GetTypeNamespaces(f.FieldType));
                }
                else if (mb is PropertyInfo) {
                    var p = (PropertyInfo) mb;
                    if (IsBaseType(p.PropertyType))
                        continue;
                    res.AddRange(GetTypeNamespaces(p.PropertyType));
                }
            }
            return res.Distinct().ToList();
        }

        private static bool IsBaseType(Type t) {
            return t.IsPrimitive || t == typeof(string) || t == typeof(DateTime) || t == typeof(Decimal);
        }

        private const string NameOfImplementationField = "_Implementation";

        static CodeMemberMethod CreateMethod(string methodName, ParameterInfo[] arguments, Type returnType) {
            var callMethod = new CodeMemberMethod();
            callMethod.Attributes = MemberAttributes.Public | MemberAttributes.Final;
            callMethod.Name = methodName;
            callMethod.Parameters.AddRange(arguments
                .Select(a => new CodeParameterDeclarationExpression(GetProxyPType(a.ParameterType), a.Name)).ToArray());
            callMethod.ReturnType = new CodeTypeReference(GetProxyPType(returnType));

            var ImplRef =
                new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), NameOfImplementationField);

            var listBuilderParams = new List<CodeExpression>();

            foreach (var arg in arguments) {
                var argRef = new CodeArgumentReferenceExpression(arg.Name);
                if (BinFormater.IsPrimitive(arg.ParameterType)) {
                    listBuilderParams.Add(argRef);
                }
                else {
                    var deserializeArg = new CodeMethodInvokeExpression(
                        new CodeTypeReferenceExpression(typeof(OSerializer)), nameof(OSerializer.Deserialize), argRef,
                        new CodeTypeOfExpression(arg.ParameterType));
                    listBuilderParams.Add(new CodeCastExpression(arg.ParameterType, deserializeArg));
                }
            }

            var invokeImpl = new CodeMethodInvokeExpression(new CodeMethodReferenceExpression(ImplRef, methodName),
                listBuilderParams.ToArray());


            if (returnType == typeof(void)) {
                callMethod.Statements.Add(invokeImpl);
            }
            else if (BinFormater.IsPrimitive(returnType)) {
                callMethod.Statements.Add(new CodeMethodReturnStatement(invokeImpl));
            }
            else {
                var serializeResult = new CodeMethodInvokeExpression(
                    new CodeTypeReferenceExpression(typeof(OSerializer)), nameof(OSerializer.SerializeOnly),
                    invokeImpl);
                callMethod.Statements.Add(new CodeMethodReturnStatement(serializeResult));
            }

            return callMethod;
        }

        static Type GetProxyPType(Type t) {
            return BinFormater.IsPrimitive(t) || t == typeof(void) ? t : typeof(Cons);
        }

        static CodeConstructor CreateCtor(Type implementationType, List<MethodDescriptors> descriptors) {
            var constructor = new CodeConstructor();
            constructor.Attributes = MemberAttributes.Public | MemberAttributes.Final;

            const string nameOfImplArg = "implementation";
            const string nameOfEnvArg = "env";

            constructor.Parameters.Add(new CodeParameterDeclarationExpression(implementationType, nameOfImplArg));
            constructor.Parameters.Add(new CodeParameterDeclarationExpression(typeof(IEnviromentFrame), nameOfEnvArg));

            var tcpClientReference =
                new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), NameOfImplementationField);
            constructor.Statements.Add(new CodeAssignStatement(tcpClientReference,
                new CodeArgumentReferenceExpression(nameOfImplArg)));

            foreach (var desc in descriptors) {
                var funcTypes = desc.Arguments.Select(p => GetProxyPType(p.ParameterType))
                    .Concat(new[] {GetProxyPType(desc.ReturnType)}).ToArray();
                var createDlgType = new CodeMethodInvokeExpression(new CodeTypeReferenceExpression(typeof(Expression)),
                    nameof(Expression.GetDelegateType), funcTypes.Select(t => new CodeTypeOfExpression(t)).ToArray());
                var createInvokableDlg = new CodeMethodInvokeExpression(
                    new CodeTypeReferenceExpression(typeof(Delegate)), nameof(Delegate.CreateDelegate), createDlgType,
                    new CodeThisReferenceExpression(), new CodePrimitiveExpression(desc.MethodName));
                var createDefine = new CodeMethodInvokeExpression(
                    new CodeMethodReferenceExpression(new CodeArgumentReferenceExpression(nameOfEnvArg),
                        nameof(IEnviromentFrame.Define)), new CodePrimitiveExpression(desc.DefineName),
                    createInvokableDlg);

                constructor.Statements.Add(createDefine);
            }

            return constructor;
        }

        static CodeMemberField AddField(string name, Type type) {
            var field = new CodeMemberField();
            field.Attributes = MemberAttributes.Private;
            field.Name = name;
            field.Type = new CodeTypeReference(type);

            return field;
        }
    }


    [AttributeUsage(AttributeTargets.Interface, AllowMultiple = false)]
    public class EnviromentAttribute : Attribute {
        public string EnviromentName { get; }

        public EnviromentAttribute(string enviromentName) {
            EnviromentName = enviromentName;
        }
    }
}
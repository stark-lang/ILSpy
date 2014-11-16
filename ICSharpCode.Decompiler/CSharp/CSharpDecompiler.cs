﻿// Copyright (c) 2014 Daniel Grunwald
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.CSharp.Refactoring;
using ICSharpCode.NRefactory.TypeSystem;
using ICSharpCode.NRefactory.TypeSystem.Implementation;
using ICSharpCode.NRefactory.Utils;
using Mono.Cecil;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;

namespace ICSharpCode.Decompiler.CSharp
{
	public class CSharpDecompiler
	{
		readonly DecompilerTypeSystem typeSystem;
		TypeSystemAstBuilder typeSystemAstBuilder;
		List<IAstTransform> astTransforms;

		public CancellationToken CancellationToken { get; set; }

		/// <summary>
		/// C# AST transforms.
		/// </summary>
		public IList<IAstTransform> AstTransforms {
			get { return astTransforms; }
		}

		public CSharpDecompiler(ModuleDefinition module)
			: this(new DecompilerTypeSystem(module))
		{
		}

		public CSharpDecompiler(DecompilerTypeSystem typeSystem)
		{
			if (typeSystem == null)
				throw new ArgumentNullException("typeSystem");
			this.typeSystem = typeSystem;

			astTransforms = new List<IAstTransform> {
				//new PushNegation(),
				//new DelegateConstruction(context),
				//new PatternStatementTransform(context),
				new ReplaceMethodCallsWithOperators(),
				new IntroduceUnsafeModifier(),
				new AddCheckedBlocks(),
				//new DeclareVariables(context), // should run after most transforms that modify statements
				new ConvertConstructorCallIntoInitializer(typeSystem), // must run after DeclareVariables
				//new DecimalConstantTransform(),
				//new IntroduceUsingDeclarations(context),
				//new IntroduceExtensionMethods(context), // must run after IntroduceUsingDeclarations
				//new IntroduceQueryExpressions(context), // must run after IntroduceExtensionMethods
				//new CombineQueryExpressions(context),
				//new FlattenSwitchBlocks(),
			};

			typeSystemAstBuilder = new TypeSystemAstBuilder();
			typeSystemAstBuilder.AlwaysUseShortTypeNames = true;
			typeSystemAstBuilder.AddAnnotations = true;
		}
		
		void RunTransforms(AstNode rootNode)
		{
			foreach (var transform in astTransforms)
				transform.Run(rootNode);
			rootNode.AcceptVisitor(new InsertParenthesesVisitor { InsertParenthesesForReadability = true });
		}
		
		public SyntaxTree DecompileWholeModuleAsSingleFile()
		{
			SyntaxTree syntaxTree = new SyntaxTree();
			foreach (var g in typeSystem.Compilation.MainAssembly.TopLevelTypeDefinitions.GroupBy(t => t.Namespace)) {
				AstNode groupNode;
				if (string.IsNullOrEmpty(g.Key)) {
					groupNode = syntaxTree;
				} else {
					NamespaceDeclaration ns = new NamespaceDeclaration(g.Key);
					syntaxTree.AddChild(ns, SyntaxTree.MemberRole);
					groupNode = ns;
				}
				
				foreach (var typeDef in g) {
					if (typeDef.Name == "<Module>" && typeDef.Members.Count == 0)
						continue;
					var typeDecl = DoDecompile(typeDef);
					groupNode.AddChild(typeDecl, SyntaxTree.MemberRole);
				}
			}
			RunTransforms(syntaxTree);
			return syntaxTree;
		}
		
		public EntityDeclaration Decompile(TypeDefinition typeDefinition)
		{
			if (typeDefinition == null)
				throw new ArgumentNullException("typeDefinition");
			ITypeDefinition typeDef = typeSystem.GetType(typeDefinition).GetDefinition();
			if (typeDef == null)
				throw new InvalidOperationException("Could not find type definition in NR type system");
			var decl = DoDecompile(typeDef);
			RunTransforms(decl);
			return decl;
		}
		
		EntityDeclaration DoDecompile(ITypeDefinition typeDef)
		{
			var entityDecl = typeSystemAstBuilder.ConvertEntity(typeDef);
			var typeDecl = entityDecl as TypeDeclaration;
			if (typeDecl == null) {
				// e.g. DelegateDeclaration
				return entityDecl;
			}
			foreach (var method in typeDef.Methods) {
				var methodDef = typeSystem.GetCecil(method) as MethodDefinition;
				if (methodDef != null) {
					var memberDecl = DoDecompile(methodDef, method);
					typeDecl.Members.Add(memberDecl);
				}
			}
			return typeDecl;
		}
		
		public EntityDeclaration Decompile(MethodDefinition methodDefinition)
		{
			if (methodDefinition == null)
				throw new ArgumentNullException("methodDefinition");
			var method = typeSystem.GetMethod(methodDefinition);
			if (method == null)
				throw new InvalidOperationException("Could not find method in NR type system");
			var decl = DoDecompile(methodDefinition, method);
			RunTransforms(decl);
			return decl;
		}

		EntityDeclaration DoDecompile(MethodDefinition methodDefinition, IMethod method)
		{
			var entityDecl = typeSystemAstBuilder.ConvertEntity(method);
			if (methodDefinition.HasBody) {
				var ilReader = new ILReader(typeSystem);
				var function = ilReader.ReadIL(methodDefinition.Body, CancellationToken);
				function.CheckInvariant();
				function.Body = function.Body.AcceptVisitor(new TransformingVisitor());
				function.CheckInvariant();
				var statementBuilder = new StatementBuilder(method);
				var body = statementBuilder.ConvertAsBlock(function.Body);
				
				// insert variables at start of body
				Statement prevVarDecl = null;
				foreach (var v in function.Variables) {
					if (v.Kind == VariableKind.Local) {
						var type = typeSystemAstBuilder.ConvertType(v.Type);
						var varDecl = new VariableDeclarationStatement(type, v.Name);
						body.Statements.InsertAfter(prevVarDecl, varDecl);
						prevVarDecl = varDecl;
					}
				}
				
				entityDecl.AddChild(body, Roles.Body);
			}
			return entityDecl;
		}
	}
}
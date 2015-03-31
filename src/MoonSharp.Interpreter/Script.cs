﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MoonSharp.Interpreter.CoreLib;
using MoonSharp.Interpreter.DataStructs;
using MoonSharp.Interpreter.Debugging;
using MoonSharp.Interpreter.Diagnostics;
using MoonSharp.Interpreter.Execution;
using MoonSharp.Interpreter.Execution.VM;
using MoonSharp.Interpreter.Interop;
using MoonSharp.Interpreter.IO;
using MoonSharp.Interpreter.Platforms;
using MoonSharp.Interpreter.Tree;
using MoonSharp.Interpreter.Tree.Expressions;
using MoonSharp.Interpreter.Tree.Fast_Interface;
using MoonSharp.Interpreter.Tree.Statements;

namespace MoonSharp.Interpreter
{
	/// <summary>
	/// This class implements a MoonSharp scripting session. Multiple Script objects can coexist in the same program but cannot share
	/// data among themselves unless some mechanism is put in place.
	/// </summary>
	public class Script
	{
		/// <summary>
		/// The version of the MoonSharp engine
		/// </summary>
		public const string VERSION = "0.9.1.0"; 

		/// <summary>
		/// The Lua version being supported
		/// </summary>
		public const string LUA_VERSION = "5.2";

		Processor m_MainProcessor = null;
		ByteCode m_ByteCode;
		List<SourceCode> m_Sources = new List<SourceCode>();
		Table m_GlobalTable;
		IDebugger m_Debugger;
		Table[] m_TypeMetatables = new Table[(int)LuaTypeExtensions.MaxMetaTypes];

		/// <summary>
		/// Initializes the <see cref="Script"/> class.
		/// </summary>
		static Script()
		{
			GlobalOptions = new ScriptGlobalOptions();

			DefaultOptions = new ScriptOptions()
			{
				DebugPrint = s => { Script.GlobalOptions.Platform.DefaultPrint(s); },
				DebugInput = s => { return Script.GlobalOptions.Platform.DefaultInput(s); },
				CheckThreadAccess = true,
				ScriptLoader = PlatformAutoDetector.GetDefaultScriptLoader(),
				TailCallOptimizationThreshold = 65536
			};
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="Script"/> class.
		/// </summary>
		public Script()
			: this(CoreModules.Preset_Default)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="Script"/> class.
		/// </summary>
		/// <param name="coreModules">The core modules to be pre-registered in the default global table.</param>
		public Script(CoreModules coreModules)
		{
			Options = new ScriptOptions(DefaultOptions);
			PerformanceStats = new PerformanceStatistics();
			Registry = new Table(this);

			m_ByteCode = new ByteCode(this);
			m_GlobalTable = new Table(this).RegisterCoreModules(coreModules);
			m_MainProcessor = new Processor(this, m_GlobalTable, m_ByteCode);
		}


		/// <summary>
		/// Gets or sets the script loader which will be used as the value of the
		/// ScriptLoader property for all newly created scripts.
		/// </summary>
		public static ScriptOptions DefaultOptions { get; private set; }

		/// <summary>
		/// Gets access to the script options. 
		/// </summary>
		public ScriptOptions Options { get; private set; }

		/// <summary>
		/// Gets the global options, that is options which cannot be customized per-script.
		/// </summary>
		public static ScriptGlobalOptions GlobalOptions { get; private set; }

		/// <summary>
		/// Gets access to performance statistics.
		/// </summary>
		public PerformanceStatistics PerformanceStats { get; private set; }

		/// <summary>
		/// Gets the default global table for this script. Unless a different table is intentionally passed (or setfenv has been used)
		/// execution uses this table.
		/// </summary>
		public Table Globals
		{
			get { return m_GlobalTable; }
		}

		/// <summary>
		/// Loads a string containing a Lua/MoonSharp function.
		/// </summary>
		/// <param name="code">The code.</param>
		/// <param name="globalTable">The global table to bind to this chunk.</param>
		/// <param name="funcFriendlyName">Name of the function used to report errors, etc.</param>
		/// <returns>
		/// A DynValue containing a function which will execute the loaded code.
		/// </returns>
		public DynValue LoadFunction(string code, Table globalTable = null, string funcFriendlyName = null)
		{
			string chunkName = string.Format("libfunc_{0}", funcFriendlyName ?? m_Sources.Count.ToString());

			SourceCode source = new SourceCode(chunkName, code, m_Sources.Count, this);

			m_Sources.Add(source);

			int address = Loader_Fast.LoadFunction(this, source, m_ByteCode, globalTable ?? m_GlobalTable);

			SignalSourceCodeChange(source);
			SignalByteCodeChange();

			return MakeClosure(address);
		}

		private void SignalByteCodeChange()
		{
			if (m_Debugger != null)
			{
				m_Debugger.SetByteCode(m_ByteCode.Code.Select(s => s.ToString()).ToArray());
			}
		}

		private void SignalSourceCodeChange(SourceCode source)
		{
			if (m_Debugger != null)
			{
				m_Debugger.SetSourceCode(source);
			}
		}


		/// <summary>
		/// Loads a string containing a Lua/MoonSharp script.
		/// </summary>
		/// <param name="code">The code.</param>
		/// <param name="globalTable">The global table to bind to this chunk.</param>
		/// <param name="codeFriendlyName">Name of the code - used to report errors, etc.</param>
		/// <returns>
		/// A DynValue containing a function which will execute the loaded code.
		/// </returns>
		public DynValue LoadString(string code, Table globalTable = null, string codeFriendlyName = null)
		{
			if (code.StartsWith(StringModule.BASE64_DUMP_HEADER))
			{
				code = code.Substring(StringModule.BASE64_DUMP_HEADER.Length);
				byte[] data = Convert.FromBase64String(code);
				using (MemoryStream ms = new MemoryStream(data))
					return LoadStream(ms, globalTable, codeFriendlyName);
			}

			string chunkName = string.Format("{0}", codeFriendlyName ?? "chunk_" + m_Sources.Count.ToString());

			SourceCode source = new SourceCode(codeFriendlyName ?? chunkName, code, m_Sources.Count, this);

			m_Sources.Add(source);

			int address = Loader_Fast.LoadChunk(this,
				source,
				m_ByteCode,
				globalTable ?? m_GlobalTable);

			SignalSourceCodeChange(source);
			SignalByteCodeChange();

			return MakeClosure(address);
		}

		/// <summary>
		/// Loads a Lua/MoonSharp script from a System.IO.Stream. NOTE: This will *NOT* close the stream!
		/// </summary>
		/// <param name="stream">The stream containing code.</param>
		/// <param name="globalTable">The global table to bind to this chunk.</param>
		/// <param name="codeFriendlyName">Name of the code - used to report errors, etc.</param>
		/// <returns>
		/// A DynValue containing a function which will execute the loaded code.
		/// </returns>
		public DynValue LoadStream(Stream stream, Table globalTable = null, string codeFriendlyName = null)
		{
			Stream codeStream = new UndisposableStream(stream);

			if (!Processor.IsDumpStream(codeStream))
			{
				using (StreamReader sr = new StreamReader(codeStream))
				{
					string scriptCode = sr.ReadToEnd();
					return LoadString(scriptCode, globalTable, codeFriendlyName);
				}
			}
			else
			{
				string chunkName = string.Format("{0}", codeFriendlyName ?? "dump_" + m_Sources.Count.ToString());

				SourceCode source = new SourceCode(codeFriendlyName ?? chunkName, 
					string.Format("-- This script was decoded from a binary dump - dump_{0}", m_Sources.Count),
					m_Sources.Count, this);

				m_Sources.Add(source);

				bool hasUpvalues;
				int address = m_MainProcessor.Undump(codeStream, m_Sources.Count - 1, globalTable ?? m_GlobalTable, out hasUpvalues);

				SignalSourceCodeChange(source);
				SignalByteCodeChange();

				if (hasUpvalues)
					return MakeClosure(address, globalTable ?? m_GlobalTable);
				else
					return MakeClosure(address);
			}
		}

		/// <summary>
		/// Dumps on the specified stream.
		/// </summary>
		/// <param name="function">The function.</param>
		/// <param name="stream">The stream.</param>
		/// <exception cref="System.ArgumentException">
		/// function arg is not a function!
		/// or
		/// stream is readonly!
		/// or
		/// function arg has upvalues other than _ENV
		/// </exception>
		public void Dump(DynValue function, Stream stream)
		{
			if (function.Type != DataType.Function)
				throw new ArgumentException("function arg is not a function!");

			if (!stream.CanWrite)
				throw new ArgumentException("stream is readonly!");

			Closure.UpvaluesType upvaluesType = function.Function.GetUpvaluesType();

			if (upvaluesType == Closure.UpvaluesType.Closure)
				throw new ArgumentException("function arg has upvalues other than _ENV");

			UndisposableStream outStream = new UndisposableStream(stream);
			m_MainProcessor.Dump(outStream, function.Function.EntryPointByteCodeLocation, upvaluesType == Closure.UpvaluesType.Environment);
		}


		/// <summary>
		/// Loads a string containing a Lua/MoonSharp script.
		/// </summary>
		/// <param name="filename">The code.</param>
		/// <param name="globalContext">The global table to bind to this chunk.</param>
		/// <param name="friendlyFilename">The filename to be used in error messages.</param>
		/// <returns>
		/// A DynValue containing a function which will execute the loaded code.
		/// </returns>
		public DynValue LoadFile(string filename, Table globalContext = null, string friendlyFilename = null)
		{
			#pragma warning disable 618
			filename = Options.ScriptLoader.ResolveFileName(filename, globalContext ?? m_GlobalTable);
			#pragma warning restore 618

			object code = Options.ScriptLoader.LoadFile(filename, globalContext ?? m_GlobalTable);

			if (code is string)
			{
				return LoadString((string)code, globalContext, friendlyFilename ?? filename);
			}
			else if (code is byte[])
			{
				using(MemoryStream ms = new MemoryStream((byte[])code))
					return LoadStream(ms, globalContext, friendlyFilename ?? filename);
			}
			else if (code is Stream)
			{
				try
				{
					return LoadStream((Stream)code, globalContext, friendlyFilename ?? filename);
				}
				finally
				{
					((Stream)code).Dispose();
				}
			}
			else
			{
				if (code == null)
					throw new InvalidCastException("Unexpected null from IScriptLoader.LoadFile");
				else
					throw new InvalidCastException(string.Format("Unsupported return type from IScriptLoader.LoadFile : {0}", code.GetType()));
			}
		}


		/// <summary>
		/// Loads and executes a string containing a Lua/MoonSharp script.
		/// </summary>
		/// <param name="code">The code.</param>
		/// <param name="globalContext">The global context.</param>
		/// <returns>
		/// A DynValue containing the result of the processing of the loaded chunk.
		/// </returns>
		public DynValue DoString(string code, Table globalContext = null)
		{
			DynValue func = LoadString(code, globalContext);
			return Call(func);
		}


		/// <summary>
		/// Loads and executes a stream containing a Lua/MoonSharp script.
		/// </summary>
		/// <param name="stream">The stream.</param>
		/// <param name="globalContext">The global context.</param>
		/// <returns>
		/// A DynValue containing the result of the processing of the loaded chunk.
		/// </returns>
		public DynValue DoStream(Stream stream, Table globalContext = null)
		{
			DynValue func = LoadStream(stream, globalContext);
			return Call(func);
		}


		/// <summary>
		/// Loads and executes a file containing a Lua/MoonSharp script.
		/// </summary>
		/// <param name="filename">The filename.</param>
		/// <param name="globalContext">The global context.</param>
		/// <returns>
		/// A DynValue containing the result of the processing of the loaded chunk.
		/// </returns>
		public DynValue DoFile(string filename, Table globalContext = null)
		{
			DynValue func = LoadFile(filename, globalContext);
			return Call(func);
		}


		/// <summary>
		/// Runs the specified file with all possible defaults for quick experimenting.
		/// </summary>
		/// <param name="filename">The filename.</param>
		/// A DynValue containing the result of the processing of the executed script.
		public static DynValue RunFile(string filename)
		{
			Script S = new Script();
			return S.DoFile(filename);
		}

		/// <summary>
		/// Runs the specified code with all possible defaults for quick experimenting.
		/// </summary>
		/// <param name="code">The Lua/MoonSharp code.</param>
		/// A DynValue containing the result of the processing of the executed script.
		public static DynValue RunString(string code)
		{
			Script S = new Script();
			return S.DoString(code);
		}

		/// <summary>
		/// Creates a closure from a bytecode address.
		/// </summary>
		/// <param name="address">The address.</param>
		/// <param name="envTable">The env table to create a 0-upvalue</param>
		/// <returns></returns>
		private DynValue MakeClosure(int address, Table envTable = null)
		{
			Closure c;

			if (envTable == null)
				c = new Closure(this, address, new SymbolRef[0], new DynValue[0]);
			else
			{
				var syms = new SymbolRef[1] {
					new SymbolRef() { i_Env = null, i_Index= 0, i_Name = WellKnownSymbols.ENV, i_Type =  SymbolRefType.DefaultEnv },
				};

				var vals = new DynValue[1] {
					DynValue.NewTable(envTable)
				};

				c = new Closure(this, address, syms, vals);
			}

			return DynValue.NewClosure(c);
		}

		/// <summary>
		/// Calls the specified function.
		/// </summary>
		/// <param name="function">The Lua/MoonSharp function to be called</param>
		/// <returns>
		/// The return value(s) of the function call.
		/// </returns>
		/// <exception cref="System.ArgumentException">Thrown if function is not of DataType.Function</exception>
		public DynValue Call(DynValue function)
		{
			return Call(function, new DynValue[0]);
		}

		/// <summary>
		/// Calls the specified function.
		/// </summary>
		/// <param name="function">The Lua/MoonSharp function to be called</param>
		/// <param name="args">The arguments to pass to the function.</param>
		/// <returns>
		/// The return value(s) of the function call.
		/// </returns>
		/// <exception cref="System.ArgumentException">Thrown if function is not of DataType.Function</exception>
		public DynValue Call(DynValue function, params DynValue[] args)
		{
			if (function.Type == DataType.ClrFunction)
				return function.Callback.ClrCallback(this.CreateDynamicExecutionContext(function.Callback), new CallbackArguments(args, false));

			if (function.Type != DataType.Function)
				throw new ArgumentException("function is not of DataType.Function");

			return m_MainProcessor.Call(function, args);
		}

		/// <summary>
		/// Calls the specified function.
		/// </summary>
		/// <param name="function">The Lua/MoonSharp function to be called</param>
		/// <param name="args">The arguments to pass to the function.</param>
		/// <returns>
		/// The return value(s) of the function call.
		/// </returns>
		/// <exception cref="System.ArgumentException">Thrown if function is not of DataType.Function</exception>
		public DynValue Call(DynValue function, params object[] args)
		{
			DynValue[] dargs = new DynValue[args.Length];

			for (int i = 0; i < dargs.Length; i++)
				dargs[i] = DynValue.FromObject(this, args[i]);

			return Call(function, dargs);
		}

		/// <summary>
		/// Calls the specified function.
		/// </summary>
		/// <param name="function">The Lua/MoonSharp function to be called</param>
		/// <returns></returns>
		/// <exception cref="System.ArgumentException">Thrown if function is not of DataType.Function</exception>
		public DynValue Call(object function)
		{
			return Call(DynValue.FromObject(this, function));
		}

		/// <summary>
		/// Calls the specified function.
		/// </summary>
		/// <param name="function">The Lua/MoonSharp function to be called </param>
		/// <param name="args">The arguments to pass to the function.</param>
		/// <returns></returns>
		/// <exception cref="System.ArgumentException">Thrown if function is not of DataType.Function</exception>
		public DynValue Call(object function, params object[] args)
		{
			return Call(DynValue.FromObject(this, function), args);
		}

		/// <summary>
		/// Creates a coroutine pointing at the specified function.
		/// </summary>
		/// <param name="function">The function.</param>
		/// <returns>
		/// The coroutine handle.
		/// </returns>
		/// <exception cref="System.ArgumentException">Thrown if function is not of DataType.Function or DataType.ClrFunction</exception>
		public DynValue CreateCoroutine(DynValue function)
		{
			if (function.Type == DataType.Function)
				return m_MainProcessor.Coroutine_Create(function.Function);
			else if (function.Type == DataType.ClrFunction)
				return DynValue.NewCoroutine(new Coroutine(function.Callback));
			else
				throw new ArgumentException("function is not of DataType.Function or DataType.ClrFunction");
		}

		/// <summary>
		/// Creates a coroutine pointing at the specified function.
		/// </summary>
		/// <param name="function">The function.</param>
		/// <returns>
		/// The coroutine handle.
		/// </returns>
		/// <exception cref="System.ArgumentException">Thrown if function is not of DataType.Function or DataType.ClrFunction</exception>
		public DynValue CreateCoroutine(object function)
		{
			return CreateCoroutine(DynValue.FromObject(this, function));
		}


		/// <summary>
		/// Gets the main chunk function.
		/// </summary>
		/// <returns>A DynValue containing a function which executes the first chunk that has been loaded.</returns>
		public DynValue GetMainChunk()
		{
			return MakeClosure(0);
		}

		/// <summary>
		/// Attaches a debugger. This usually should be called by the debugger itself and not by user code.
		/// </summary>
		/// <param name="debugger">The debugger object.</param>
		public void AttachDebugger(IDebugger debugger)
		{
			m_Debugger = debugger;
			m_MainProcessor.AttachDebugger(debugger);

			foreach (SourceCode src in m_Sources)
				SignalSourceCodeChange(src);

			SignalByteCodeChange();
		}

		/// <summary>
		/// Gets the source code.
		/// </summary>
		/// <param name="sourceCodeID">The source code identifier.</param>
		/// <returns></returns>
		public SourceCode GetSourceCode(int sourceCodeID)
		{
			return m_Sources[sourceCodeID];
		}


		/// <summary>
		/// Gets the source code count.
		/// </summary>
		/// <value>
		/// The source code count.
		/// </value>
		public int SourceCodeCount
		{
			get { return m_Sources.Count; }
		}



		/// <summary>
		/// Loads a module as per the "require" Lua function. http://www.lua.org/pil/8.1.html
		/// </summary>
		/// <param name="modname">The module name</param>
		/// <param name="globalContext">The global context.</param>
		/// <returns></returns>
		/// <exception cref="ScriptRuntimeException">Raised if module is not found</exception>
		public DynValue RequireModule(string modname, Table globalContext = null)
		{
			Table globals = globalContext ?? m_GlobalTable;
			string filename = Options.ScriptLoader.ResolveModuleName(modname, globals);

			if (filename == null)
				throw new ScriptRuntimeException("module '{0}' not found", modname);

			DynValue func = LoadFile(filename, globalContext, filename);
			return func;
		}



		/// <summary>
		/// Gets a type metatable.
		/// </summary>
		/// <param name="type">The type.</param>
		/// <returns></returns>
		public Table GetTypeMetatable(DataType type)
		{
			int t = (int)type;

			if (t >= 0 && t < m_TypeMetatables.Length)
				return m_TypeMetatables[t];

			return null;
		}

		/// <summary>
		/// Sets a type metatable.
		/// </summary>
		/// <param name="type">The type. Must be Nil, Boolean, Number, String or Function</param>
		/// <param name="metatable">The metatable.</param>
		/// <exception cref="System.ArgumentException">Specified type not supported :  + type.ToString()</exception>
		public void SetTypeMetatable(DataType type, Table metatable)
		{
			int t = (int)type;

			if (t >= 0 && t < m_TypeMetatables.Length)
				m_TypeMetatables[t] = metatable;
			else
				throw new ArgumentException("Specified type not supported : " + type.ToString());
		}


		/// <summary>
		/// Warms up the parser/lexer structures so that MoonSharp operations start faster.
		/// </summary>
		public static void WarmUp()
		{
			Script s = new Script(CoreModules.Basic);
			s.LoadString("return 1;");
		}


		/// <summary>
		/// Creates a new dynamic expression.
		/// </summary>
		/// <param name="code">The code of the expression.</param>
		/// <returns></returns>
		public DynamicExpression CreateDynamicExpression(string code)
		{
			DynamicExprExpression dee = Loader_Fast.LoadDynamicExpr(this, new SourceCode("__dynamic", code, -1, this));
			return new DynamicExpression(this, code, dee);
		}

		/// <summary>
		/// Creates a new dynamic expression which is actually quite static, returning always the same constant value.
		/// </summary>
		/// <param name="code">The code of the not-so-dynamic expression.</param>
		/// <param name="constant">The constant to return.</param>
		/// <returns></returns>
		public DynamicExpression CreateConstantDynamicExpression(string code, DynValue constant)
		{
			return new DynamicExpression(this, code, constant);
		}

		/// <summary>
		/// Gets an execution context exposing only partial functionality, which should be used for
		/// those cases where the execution engine is not really running - for example for dynamic expression
		/// or calls from CLR to CLR callbacks
		/// </summary>
		internal ScriptExecutionContext CreateDynamicExecutionContext(CallbackFunction func = null)
		{
			return new ScriptExecutionContext(m_MainProcessor, func, null, isDynamic : true);
		}

		/// <summary>
		/// MoonSharp (like Lua itself) provides a registry, a predefined table that can be used by any CLR code to 
		/// store whatever Lua values it needs to store. 
		/// Any CLR code can store data into this table, but it should take care to choose keys 
		/// that are different from those used by other libraries, to avoid collisions. 
		/// Typically, you should use as key a string GUID, a string containing your library name, or a 
		/// userdata with the address of a CLR object in your code.
		/// </summary>
		public Table Registry
		{
			get;
			private set;
		}



	}
}

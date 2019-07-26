﻿
namespace Ceras.Formatters
{
	using Ceras.Helpers;
	using Resolvers;
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq.Expressions;
	using System.Runtime.CompilerServices;
	using System.Threading;


#if FAST_EXP
	using FastExpressionCompiler;
#endif

	/*
	 * 
	 * This formatter enables dealing with any object graph, even cyclic references of any kind.
	 * It also save binary size(and thus a lot of cpu cycles as well if the object is large)
	 * 
	 * Important: Different CacheFormatters MUST share a common pool!
	 * Why?
	 * because what if we have an object of a known type (field type and actual type are exactly the same)
	 * and then later we encounter an 'object Field;' with a refernce to the previous object (the one in the field where the type matches).
	 * Of course the references are supposed to match in the end again, and without all objects being part of the same cache that won't work.
	 * (We would try to find the referenced object in the <object> pool but it was put into the <specific> pool when it was first encountered!)
	 * 
	 * todo: there are a few cases in here where we can certainly optimize performance
	 *		 - We can make it so _dispatchers is only used at most once per call. Currently we might have multiple lookups, but all
	 *		   we actually need is getting(or creating) the DispatcherEntry and then only work with that.
	 *		 - globalObjectConstructors is supposed to cache generic constructors, but we always check if there is a user-factory-method, which is not needed.
	 *		   we could compile that into the constructor, but at that point it is not a global ctor anymore, which is fine if we only cache it into the dispatcherEntry.
	 *
	 *
	 * todo: we might be able to eliminate the following check:
	 *			if (value is IExternalRootObject externalObj)
	 *		 by moving it out of here and into the DynamicFormatter.
	 *		 There we know what concrete type we're dealing with and if it is an IExternalObject.
	 *		 So that way we only have to check if the current object is equal to the current root object, easy!
	 *		 For deserialization we can statically compile in the check for the external resolver as well!
	 *			Downside: users that write their own formatters would have to manually take care of external object serialization, which is not cool
	 *
	 * todo: statically compile everything
	 *		we have many variables based on the specificType that will never ever change.
	 *		instead of having Serialize/Deserialize do those checks all the time, we could compile everything into a delegate.
	 *		Then we'd merge all our "sub-delegates" (like ctor and so on), into that one big delegate as well.
	 *		That way we'd save a lot of performance because entire if-chains would be completely gone.
	 *		We *always* have to do GetDispatcherEntry() anyway, so if we could instantly call into a super-optimized delegate, that'd be awesome.
	 *			Downside: Makes the actual code **really** hard to follow and understand, and impossible to debug.
	 *
	 */

	public sealed class ReferenceFormatter<T> : IFormatter<T>, ISchemaTaintedFormatter
where T : class
	{
		const int Null = 0;
		const int NewObject = 1; // + data
		const int NewDerivedObject = 2; // + type + data
		const int Backreference = 3; // + id
		const int ExternalObject = 4; // + id
		const int InlineType = 5; // + type

		readonly CerasSerializer _ceras;
		readonly TypeFormatter _typeFormatter;
		readonly TypeDictionary<DispatcherEntry> _dispatchers = new TypeDictionary<DispatcherEntry>();
		readonly bool _allowReferences;


		public ReferenceFormatter(CerasSerializer ceras)
		{
			_ceras = ceras;

			if (typeof(T).IsStatic())
				throw new InvalidOperationException("static");

			_typeFormatter = (TypeFormatter)ceras.GetSpecificFormatter(typeof(Type));

			_allowReferences = _ceras.Config.PreserveReferences;
		}


		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			//
			// Null
			if (value is null)
			{
				SerializerBinary.WriteByte(ref buffer, ref offset, Null);
				return;
			}

			var specificType = value.GetType();
			var entry = GetOrCreateEntry(specificType);

			//
			// Type
			if (entry.IsType) // This is very rare, so we cache the check itself, and do the cast below
			{
				SerializerBinary.WriteByte(ref buffer, ref offset, InlineType);
				_typeFormatter.Serialize(ref buffer, ref offset, (Type)(object)value);
				return;
			}

			//
			// ExternalObject
			if (entry.IsExternalRootObject)
			{
				var externalObj = (IExternalRootObject)value;

				if (!ReferenceEquals(_ceras.InstanceData.CurrentRoot, value))
				{
					SerializerBinary.WriteByte(ref buffer, ref offset, ExternalObject);

					var refId = externalObj.GetReferenceId();
					SerializerBinary.WriteUInt32Fixed(ref buffer, ref offset, (uint)refId);

					_ceras.Config.OnExternalObject?.Invoke(externalObj);

					return;
				}
			}

			//
			// Make reference available
			if (_allowReferences)
				if (_ceras.InstanceData.ObjectCache.GetObjectIdOrRegister(value, out int id))
				{
					// Existing value
					SerializerBinary.WriteByte(ref buffer, ref offset, Backreference);
					SerializerBinary.WriteUInt32Fixed(ref buffer, ref offset, (uint)id);
					return;
				}

			//
			// Write actual value
			if (ReferenceEquals(typeof(T), specificType))
			{
				// Code: Same Type
				SerializerBinary.WriteByte(ref buffer, ref offset, NewObject);
			}
			else
			{
				// Code: Derived Type
				SerializerBinary.WriteByte(ref buffer, ref offset, NewDerivedObject);
				_typeFormatter.Serialize(ref buffer, ref offset, specificType);
			}

			// Write object
			entry.CurrentSerializeDispatcher(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			var code = SerializerBinary.ReadByte(buffer, ref offset);
			Type specificType = typeof(T);

			switch (code)
			{
			case Null:
				// The data says that the value should be null.
				// But maybe we're recycling an object and it still contains an instance, so lets return it to the user
				if (value != null)
				{
					_ceras.DiscardObjectMethod?.Invoke(value);
					value = null;
					return;
				}
				return;

			case NewDerivedObject:
				_typeFormatter.Deserialize(buffer, ref offset, ref specificType);
				goto READ_SPECIFIC_OBJECT;

			case NewObject:
				goto READ_SPECIFIC_OBJECT;

			case Backreference:
				// Something we already know
				var objectId = (int)SerializerBinary.ReadUInt32Fixed(buffer, ref offset);
				value = _ceras.InstanceData.ObjectCache.GetExistingObject<T>(objectId);
				return;

			case ExternalObject:
				// Let the user resolve
				var externalId = (int)SerializerBinary.ReadUInt32Fixed(buffer, ref offset);
				_ceras.Config.ExternalObjectResolver.Resolve(externalId, out value);
				return;

			case InlineType:
				Type type = null;
				_typeFormatter.Deserialize(buffer, ref offset, ref type);
				value = (T)(object)type; // This is ugly, but there's no way to prevent it, right?
				return;

			default:
				throw new InvalidOperationException("Invalid Reference Code: " + code + " at index" + (offset - 1));
			}

			READ_SPECIFIC_OBJECT:

			var entry = GetOrCreateEntry(specificType);

			// At this point we know that the 'value' will not be 'null', so if 'value' (the variable) is null we need to create an instance
			if (!entry.IsValueType)
			{
				// A reference type (possibly boxed value)

				// Do we already have an object?
				if (value != null)
				{
					// Yes, then maybe we can overwrite its values (works for objects and collections)
					// But only if it's the right type!

					if (value.GetType() != specificType)
					{
						// Discard old instance
						_ceras.DiscardObjectMethod?.Invoke(value);

						// Create new instance
						value = (T)entry.Constructor();
					}
					else
					{
						// Correct Type
						// Overwrite members...
					}
				}
				else
				{
					// Instance is null, create one
					value = (T)entry.Constructor();
				}
			}
			else
			{
				// Boxed ValueType.
			}


			if (!_allowReferences)
			{
				entry.CurrentDeserializeDispatcher(buffer, ref offset, ref value);
				return;
			}


			//
			// Deserialize the object
			// 1. First generate a proxy so we can do lookups
			var objectProxy = _ceras.InstanceData.ObjectCache.CreateDeserializationProxy<T>();

			// 2. Make sure that the deserializer can make use of an already existing object (if there is one)
			objectProxy.Value = value;

			// 3. Actually read the object
			entry.CurrentDeserializeDispatcher(buffer, ref offset, ref objectProxy.Value);

			// 4. Write back the actual value, which instantly resolves all references
			value = objectProxy.Value;
		}


		DispatcherEntry GetOrCreateEntry(Type type)
		{
			ref var entry = ref _dispatchers.GetOrAddValueRef(type);
			if (entry != null)
				return entry;

			// Get type meta-data and create a dispatcher entry
			var meta = _ceras.GetTypeMetaData(type);
			entry = new DispatcherEntry(type, meta.HasSchema, meta.CurrentSchema);

			if (entry.IsType)
				return entry; // Don't need to do anything else...

			// Obtain the formatter for this specific type
			var formatter = _ceras.GetSpecificFormatter(type);

			// Create dispatchers and ctor
			if (_ceras.Config.Advanced.AotMode == AotMode.None)
			{
				entry.CurrentSerializeDispatcher = CreateSpecificSerializerDispatcher(type, formatter);
				entry.CurrentDeserializeDispatcher = CreateSpecificDeserializerDispatcher(type, formatter);
			}
			else
			{
				entry.CurrentSerializeDispatcher = CreateSpecificSerializerDispatcher_Aot(type, formatter);
				entry.CurrentDeserializeDispatcher = CreateSpecificDeserializerDispatcher_Aot(type, formatter);
			}
			entry.Constructor = Ceras.Formatters.ReferenceFormatter<T>.CreateObjectConstructor(_ceras, type);

			if (meta.HasSchema) // Framework types do not have a schemata dict
			{
				var pair = new DispatcherPair(entry.CurrentSerializeDispatcher, entry.CurrentDeserializeDispatcher);
				entry.SchemaDispatchers[entry.CurrentSchema] = pair;
			}

			return entry;
		}


		/*
		 * So what even is a SpecificDispatcher and why do we need one??
		 * 
		 * The answer is surprisingly simple.
		 * If we (the reference formatter) are of some sort of 'base type' like ReferenceFormatter<object> or ReferenceFormatter<IList> or ...
		 * then we can serialize the reference itself just fine, yea, but the actual type needs a different serializer.
		 * 
		 * There can be all sorts of actual implementations inside an 'IList' field and we can't know until we look at the current value.
		 * So that means we need to use a different formatter depending on the *actual* type of the object.
		 * 
		 * But doing the lookup from type to formatter and potentially creating one is not the only thing that needs to be done.
		 * Because there's another problem:
		 * 
		 * Our <T> would have to be co-variant and contra-variant at the same time (because we consume and produce a <T>).
		 * Of course in normal C# that's not possible because it's not even safe to do.
		 * But in our case we actually know (well, unless we get corrupted data of course) that everything will work.
		 * So to bypass that limitation we compile our own special delegate that does the forwards and backwards casting for us.
		 */
		static SerializeDelegate<T> CreateSpecificSerializerDispatcher(Type type, IFormatter specificFormatter)
		{
			// What does this method do?
			// It creates a cast+call dynamically
			// Why is that needed?
			// See this example:
			// We have a field of type 'object' containing a 'Person' instance.
			//    IFormatter<object> formatter = new ReferenceFormatter<Person>();
			// The line of code above obviously does not work since the types do not match, which is what this method fixes.

			var serializeMethod = specificFormatter.GetType().ResolveSerializeMethod(type);

			// When we have an exact type match, we can just use the method directly
			if (type == typeof(T))
				return (SerializeDelegate<T>)Delegate.CreateDelegate(typeof(SerializeDelegate<T>), (IFormatter<T>)specificFormatter, serializeMethod);



			// What we want to emulate:
			/*
			 * (buffer, offset, T value) => {
			 *	  formatter.Serialize(buffer, offset, (specificType)value);
			 */

			var refBufferArg = Expression.Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var refOffsetArg = Expression.Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = Expression.Parameter(typeof(T), "value");

			Expression convertedValueArg;

			if (typeof(T) == type)
				// Exact match
				convertedValueArg = valueArg; // todo: no need to compile a delegate at all
			else if (!type.IsValueType)
				// Cast general -> derived
				convertedValueArg = Expression.TypeAs(valueArg, type);
			else
				// Unbox
				convertedValueArg = Expression.Convert(valueArg, type);


			var body = Expression.Block(
										Expression.Call(Expression.Constant(specificFormatter), serializeMethod,
														arg0: refBufferArg,
														arg1: refOffsetArg,
														arg2: convertedValueArg)
										);

#if FAST_EXP
			var f = Expression.Lambda<SerializeDelegate<T>>(body: body, parameters: new ParameterExpression[] { refBufferArg, refOffsetArg, valueArg }).CompileFast(true);
#else
			var f = Expression.Lambda<SerializeDelegate<T>>(body: body, parameters: new ParameterExpression[] { refBufferArg, refOffsetArg, valueArg }).Compile();
#endif

			return f;
		}

		// See the comment on GetSpecificSerializerDispatcher
		static DeserializeDelegate<T> CreateSpecificDeserializerDispatcher(Type type, IFormatter specificFormatter)
		{
			var deserializeMethod = specificFormatter.GetType().ResolveDeserializeMethod(type);

			// When we have an exact type match, we can just use the method directly
			if (type == typeof(T))
				return (DeserializeDelegate<T>)Delegate.CreateDelegate(typeof(DeserializeDelegate<T>), (IFormatter<T>)specificFormatter, deserializeMethod);



			// What we want to emulate:
			/*
			 * (buffer, offset, T value) => {
			 *    (specificType) obj = (specificType)value;
			 *	  formatter.Deserialize(buffer, offset, ref obj);
			 *    value = (specificType)obj;
			 */

			var bufferArg = Expression.Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Expression.Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Expression.Parameter(typeof(T).MakeByRefType(), "value");

			var valAsSpecific = Expression.Variable(type, "valAsSpecific");

			Expression intro, outro;
			if (typeof(T) == type)
			{
				// Same type, the best case
				intro = Expression.Assign(valAsSpecific, refValueArg);
				outro = Expression.Assign(refValueArg, valAsSpecific);
			}
			else if (!typeof(T).IsValueType && type.IsValueType)
			{
				// valueType = (castToValueType)object;

				// Handle unboxing: we might have a null-value.
				intro = Expression.IfThenElse(Expression.ReferenceEqual(refValueArg, Expression.Constant(null)),
											  ifTrue: Expression.Default(type),
											  ifFalse: Expression.Unbox(refValueArg, type));

				// Box the value type again
				outro = Expression.Assign(refValueArg, Expression.Convert(valAsSpecific, typeof(T)));
			}
			else
			{
				// Types are not equal, but there are no value-types involved.
				// Some kind of casting. Maybe the field type is an interface or 'object'
				intro = Expression.Assign(valAsSpecific, Expression.TypeAs(refValueArg, type));
				// No need to up-cast.
				outro = Expression.Assign(refValueArg, valAsSpecific);
			}


			var body = Expression.Block(variables: new[] { valAsSpecific },
										expressions: new Expression[]
										{
											intro,

											Expression.Call(Expression.Constant(specificFormatter), deserializeMethod,
															arg0: bufferArg,
															arg1: refOffsetArg,
															arg2: valAsSpecific),

											outro
										});

#if FAST_EXP
			var f = Expression.Lambda<DeserializeDelegate<T>>(body: body, parameters: new ParameterExpression[] { bufferArg, refOffsetArg, refValueArg }).CompileFast(true);
#else
			var f = Expression.Lambda<DeserializeDelegate<T>>(body: body, parameters: new ParameterExpression[] { bufferArg, refOffsetArg, refValueArg }).Compile();
#endif
			return f;
		}



		/*
		 * Dispatching to the concrete formatter in an Aot-Runtime is pretty hard since we can't generate any code.
		 * For the polymorphic case using 'Invoke' is unavoidable.
		 * Fortunately exact type matches are much more likely! In which case we can completely avoid the invocation cost!
		*/
		static SerializeDelegate<T> CreateSpecificSerializerDispatcher_Aot(Type type, IFormatter specificFormatter)
		{
			var serializeMethod = specificFormatter.GetType().ResolveSerializeMethod(type);
			if (type == typeof(T))
			{
				var f = (IFormatter<T>)specificFormatter;

				return (SerializeDelegate<T>)Delegate.CreateDelegate(typeof(SerializeDelegate<T>), f, serializeMethod);
				// return (ref byte[] buffer, ref int offset, T value) => { f.Serialize(ref buffer, ref offset, value); };
			}

			// Can't call directly, need to invoke through reflection so T gets casted up/down correctly.
			var args = new object[3];
			return (ref byte[] buffer, ref int offset, T value) =>
			{
				args[0] = buffer;
				args[1] = offset;
				args[2] = value;
				serializeMethod.Invoke(specificFormatter, args);
				buffer = (byte[])args[0];
				offset = (int)args[1];
			};
		}

		static DeserializeDelegate<T> CreateSpecificDeserializerDispatcher_Aot(Type type, IFormatter specificFormatter)
		{
			var deserializeMethod = specificFormatter.GetType().ResolveDeserializeMethod(type);
			if (type == typeof(T))
			{
				var f = (IFormatter<T>)specificFormatter;

				return (DeserializeDelegate<T>)Delegate.CreateDelegate(typeof(DeserializeDelegate<T>), f, deserializeMethod);
				// return (ref byte[] buffer, ref int offset, T value) => { f.Serialize(ref buffer, ref offset, value); };
			}


			var args = new object[3];
			return new DeserializeDelegate<T>((byte[] buffer, ref int offset, ref T value) =>
			{
				args[0] = buffer;
				args[1] = offset;
				args[2] = value;

				deserializeMethod.Invoke(specificFormatter, args);

				offset = (int)args[1];
				value = (T)args[2];
			});
		}


		internal static Func<object> CreateObjectConstructor(CerasSerializer ceras, Type type)
		{
			if (type.IsArray)
			{
				// ArrayFormatter will create a new array
				return ReflectionHelper._nullResultDelegate;
			}
			else if (CerasSerializer.IsFormatterConstructed(type) || type.IsValueType)
			{
				// The formatter that handles this type also handles its creation, so we return null
				return ReflectionHelper._nullResultDelegate;
			}

			// Create a custom factory method, but also respect the userFactory if there is one
			var typeConfig = ceras.Config.GetTypeConfig(type, false);

			var tc = typeConfig.TypeConstruction;

			if (tc == null)
			{
				throw new InvalidOperationException($"Ceras can not serialize/deserialize the type '{type.FullName}' because it has no 'default constructor'. " +
													$"You can either set a default setting for all types (config.DefaultTypeConstructionMode) or configure it for individual types in config.ConfigType<YourType>()... For more examples take a look at the tutorial.");
			}

			if (tc is ConstructNull || tc.HasDataArguments)
			{
				// ConstructNull is obvious, but we also don't construct and object if the ctor has data arguments!
				// Why? Because this "TypeConstruction" thing is only for the ReferenceFormatter! And the reference formatter obviously can't call a ctor that has arguments!
				// That ctor will be called by the DynamicFormatter instead, while the ReferenceFormatter just passes in 'null'
				return ReflectionHelper._nullResultDelegate;
			}
			else
			{
				bool allowDynCode = ceras.Config.Advanced.AotMode == AotMode.None;
				return tc.GetRefFormatterConstructor(allowDynCode);
			}
		}


		void ISchemaTaintedFormatter.OnSchemaChanged(TypeMetaData meta)
		{
			// If we've encountered this specific type already...
			if (_dispatchers.TryGetValue(meta.Type, out var entry))
			{
				// ...then we might have some stuff for this schema of this type.
				//
				// So if we have some cached dispatchers already, we activate them.
				// If we don't have any, set them to null and they will be populated when actually needed
				if (entry.SchemaDispatchers.TryGetValue(meta.CurrentSchema, out var pair))
				{
					entry.CurrentSerializeDispatcher = pair.SerializeDispatcher;
					entry.CurrentDeserializeDispatcher = pair.DeserializeDispatcher;
				}
				else
				{
					entry.CurrentSerializeDispatcher = null;
					entry.CurrentDeserializeDispatcher = null;
				}
			}
		}

		class DispatcherEntry
		{
			public readonly Type Type;

			public Func<object> Constructor;

			public readonly bool IsType;
			public readonly bool IsExternalRootObject;
			public readonly bool IsValueType;

			public Schema CurrentSchema;
			public SerializeDelegate<T> CurrentSerializeDispatcher;
			public DeserializeDelegate<T> CurrentDeserializeDispatcher;

			public readonly Dictionary<Schema, DispatcherPair> SchemaDispatchers;

			public DispatcherEntry(Type type, bool hasSchema, Schema currentSchema)
			{
				Type = type;
				CurrentSchema = currentSchema;

				IsType = typeof(Type).IsAssignableFrom(type);
				IsExternalRootObject = typeof(IExternalRootObject).IsAssignableFrom(type);
				IsValueType = type.IsValueType;

				// We only need a dictionary when the schema can actually change, which is never the case for framework types
				if (hasSchema)
					SchemaDispatchers = new Dictionary<Schema, DispatcherPair>();
			}
		}

		readonly struct DispatcherPair
		{
			public readonly SerializeDelegate<T> SerializeDispatcher;
			public readonly DeserializeDelegate<T> DeserializeDispatcher;

			public DispatcherPair(SerializeDelegate<T> serialize, DeserializeDelegate<T> deserialize)
			{
				SerializeDispatcher = serialize;
				DeserializeDispatcher = deserialize;
			}
		}
	}

	// When T is sealed and statically known we can remove a lot of checks!
	// No derived types, no inline type, no external object
	public sealed class ReferenceFormatter_KnownSealedType<T> : IFormatter<T>
	where T : class
	{
		const int Null = 0;
		const int NewObject = 1; // + data
		const int Backreference = 3; // + id

		readonly CerasSerializer _ceras;

		readonly bool _allowReferences;

		readonly IFormatter<T> _formatter;
		readonly Func<T> _constructor;


		public ReferenceFormatter_KnownSealedType(CerasSerializer ceras)
		{
			_ceras = ceras;

			if (typeof(T).IsStatic())
				throw new InvalidOperationException("static");

			if (!typeof(T).IsSealed)
				throw new InvalidOperationException($"Type '{typeof(T).FriendlyName()}' can not be handled with this formatter because it is not sealed.");

			if (ceras.Config.VersionTolerance.Mode != VersionToleranceMode.Disabled)
				// We can't ever switch our schema, because _formatter must be readonly, because the JIT can then optimize the call to it.
				throw new InvalidOperationException($"Sealed-Type optimized ReferenceFormatter cannot be used with VersionTolerance.");

			_allowReferences = _ceras.Config.PreserveReferences;


			_formatter = (IFormatter<T>)ceras.GetSpecificFormatter(typeof(T));

			var ctor = Ceras.Formatters.ReferenceFormatter<T>.CreateObjectConstructor(_ceras, typeof(T));
			_constructor = Unsafe.As<Func<T>>(ctor);
		}


		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			// Null
			if (value is null)
			{
				SerializerBinary.WriteByte(ref buffer, ref offset, Null);
				return;
			}

			// Backreference
			if (_allowReferences)
				if (_ceras.InstanceData.ObjectCache.GetObjectIdOrRegister(value, out int id))
				{
					// Existing value
					SerializerBinary.WriteByte(ref buffer, ref offset, Backreference);
					SerializerBinary.WriteUInt32Fixed(ref buffer, ref offset, (uint)id);
					return;
				}

			// NewObject (same type)
			SerializerBinary.WriteByte(ref buffer, ref offset, NewObject);
			_formatter.Serialize(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			var code = SerializerBinary.ReadByte(buffer, ref offset);

			if (code == Null)
			{
				if (value != null)
				{
					// Ok the data tells us that value should be null, but maybe we're recycling an object and it still contains an instance.
					_ceras.DiscardObjectMethod?.Invoke(value);
					value = default;
				}
				return;
			}

			// At this point we know that the 'value' will not be 'null', so if 'value' (the variable) is null we need to create an instance
			if (value == null)
				value = _constructor();


			if (_allowReferences)
			{
				// Backreference
				if (code == Backreference)
				{
					var objectId = (int)SerializerBinary.ReadUInt32Fixed(buffer, ref offset);
					value = _ceras.InstanceData.ObjectCache.GetExistingObject<T>(objectId);
					return;
				}


				// NewObject
				// 1. First generate a proxy so we can do lookups
				var objectProxy = _ceras.InstanceData.ObjectCache.CreateDeserializationProxy<T>();

				// 2. Make sure that the deserializer can make use of an already existing object (if there is one)
				objectProxy.Value = value;

				// 3. Actually read the object
				_formatter.Deserialize(buffer, ref offset, ref objectProxy.Value);

				// 4. Write back the actual value, which instantly resolves all references
				value = objectProxy.Value;
			}
			else
			{
				_formatter.Deserialize(buffer, ref offset, ref value);
				return;
			}
		}
	}


}

/*
	Serializing an instance of 'Type':

	Let's say someone has a field like this: `object obj = typeof(Bla);`
	We don't know what's inside the field from just the field-type (which is just 'object').
	So as always, we'd have to write the type. To do that we would just call "obj.GetType()",
	but in this case that won't work because the result of 'typeof(Type).GetType()' is 'System.RuntimeType'.

	In theory we could resolve this by having a special case in the TypeFormatter (catching RuntimeType...)
	but that would complicate things a lot.
	And there is another (and even more important) problem that is not immediately apparent: sharing!
	The TypeFormatter has its own specialized cache for types, so not only could we not profit from its specialized code,
	but we would also potentially write unoptimized strings for many types!
	And if there are any actual instances of that type we'd waste even more space by encoding the type once with type-encoding
	and once as a "value".

	Solution:
	We can resolve all of those problems by making 'Type' a special case (as it should be).

	According to benchmark tests, the new check for 'Type' is actually free (below noise)in performance terms!
	So we didn't add any performance penalty AND fixed multiple problems at the same time.
*/

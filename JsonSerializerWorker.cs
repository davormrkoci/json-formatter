using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.ComponentModel;
using System.Runtime.Serialization;
using System.Reflection;
using System.IO;
using System.Runtime.Serialization.Formatters;
using Newtonsoft.Json;

namespace smg.Serializers.JSON
{
    public class JsonSerializerWorker
    {
        delegate void SetValDelegate(object val);

        class StackEntry
        {
            SetValDelegate setVal_;
            string text_;
            bool valueSet_ = false;
            public StackEntry(SetValDelegate setVal, string text)
            {
                setVal_ = setVal;
                text_ = text;
            }

            public void SetValue(object val)
            {
                if (valueSet_)
                    throw new InvalidOperationException("Value already set");

                setVal_(val);
                valueSet_ = true;
            }

            public string GetPathString()
            {
                return text_;
            }
        };

        class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            bool IEqualityComparer<object>.Equals(object x, object y)
            {
                return Object.ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return 0;
            }
        };

        JsonFormatter formatter_;
        TextWriter writer_;
        object graph_;
        Type graphType_;
        object jsRoot_;
        Stack<StackEntry> stack_ = new Stack<StackEntry>();
        Dictionary<object, string> processedObjects_ = new Dictionary<object, string>(new ReferenceEqualityComparer());

        public JsonSerializerWorker(JsonFormatter formatter, TextWriter writer, object graph, Type expectedType)
        {
            formatter_ = formatter;
            writer_ = writer;
            graph_ = graph;
            graphType_ = expectedType;

            Run();
        }

        private void Run()
        {
            stack_.Push(new StackEntry(delegate(object val) { jsRoot_ = val; }, "root"));
            SerializeValue(graph_, graphType_);
            stack_.Pop();

            if (stack_.Count != 0)
                throw new SerializationException("Internal error");

            //write out actual json data
            using (JsonWriter jsWriter = new JsonWriter(writer_))
            {
                jsWriter.Formatting = Formatting.Indented;
                (new Newtonsoft.Json.JsonSerializer()).Serialize(jsWriter, jsRoot_);
            }
        }

        private void SerializeArray(Array array, List<int> indices)
        {
            if (indices.Count >= array.Rank)
                throw new SerializationException("Internal error");

            int lowerBound = array.GetLowerBound(indices.Count);
            int length = array.GetLength(indices.Count);

            JavaScriptArray jsArray = new JavaScriptArray();
            stack_.Peek().SetValue(jsArray);
            for (int i = lowerBound; i < (lowerBound + length); ++i)
            {
                stack_.Push(new StackEntry(delegate(object val) { jsArray.Add(val); }, "[" + i + "]"));

                List<int> localIndices = new List<int>(indices);
                localIndices.Add(i);

                if (localIndices.Count < array.Rank)
                    SerializeArray(array, localIndices);
                else
                    SerializeValue(array.GetValue(localIndices.ToArray()), array.GetType().GetElementType());

                stack_.Pop();
            }
        }

        private void SerializeArray(Array array)
        {
            SerializeArray(array, new List<int>());
        }

        private void SerializeValue(object value, Type expectedType)
        {
            if (value == null)
            {
                SerializeElementary(value, expectedType == null);
                return;
            }

            Type objectType = value.GetType();

            bool needTypeInfo = false;
            if (objectType != expectedType)
                needTypeInfo = true;

            //elementary values
            if (JsonFormatter.IsElementary(objectType))
            {
                SerializeElementary(value, needTypeInfo);
                return;
            }

            //check for a circular reference
            if (!objectType.IsValueType)
            {
                if (processedObjects_.ContainsKey(value))
                {
                    JavaScriptObject jsObject = new JavaScriptObject();
                    jsObject["@@info@@"] = GetPath(stack_);
                    jsObject["@@reference@@"] = processedObjects_[value];
                    stack_.Peek().SetValue(jsObject);
                    return;
                }

                processedObjects_[value] = GetPath(stack_);
            }

            //System.Collections.Generic.Dictionary`2
            if (objectType.FullName.StartsWith("System.Collections.Generic.Dictionary`2"))
            {
                SerializeDictionary(value, objectType, needTypeInfo);
                return;
            }

            //System.Collections.Generic.List`1
            if (objectType.FullName.StartsWith("System.Collections.Generic.List`1"))
            {
                SerializeList(value, objectType, needTypeInfo);
                return;
            }

            //ISerializable
            ISerializationSurrogate surrogate = formatter_.GetSurrogate(objectType);
            if (value is ISerializable || surrogate != null)
            {
                JavaScriptObject jsObject = new JavaScriptObject();
                jsObject["@@info@@"] = GetPath(stack_);
                stack_.Peek().SetValue(jsObject);

                SerializationInfo info = new SerializationInfo(objectType, new FormatterConverter());
                if (surrogate != null)
                    surrogate.GetObjectData(value, info, formatter_.Context);
                else
                    ((ISerializable) value).GetObjectData(info, formatter_.Context);

                Type specifiedType = Type.GetType(info.FullTypeName + ", " + info.AssemblyName);
                jsObject["@@type@@"] = GetTypeName(specifiedType, formatter_.AssemblyFormat);

                SerializationInfoEnumerator e = info.GetEnumerator();
                while (e.MoveNext())
                {
                    if (jsObject.ContainsKey(e.Name))
                        throw new SerializationException();

                    stack_.Push(new StackEntry(delegate(object val) { jsObject[e.Name] = val; }, "." + e.Name));
                    SerializeValue(e.Value, typeof(object));
                    stack_.Pop();
                }
                return;
            }

            if (!objectType.IsSerializable)
                throw new SerializationException("Instances of type " + objectType.FullName + " are not serializable");

            //array
            if (objectType.IsArray)
            {
                SerializeArray(value, objectType, needTypeInfo);
                return;
            }

            //convert to string
            TypeConverter converter = formatter_.GetTypeConverter(objectType);
            if (converter.CanConvertTo(typeof(string)) && converter.CanConvertFrom(typeof(string)))
            {
                if (needTypeInfo)
                {
                    JavaScriptObject jsObject = new JavaScriptObject();
                    jsObject["@@info@@"] = GetPath(stack_);
                    jsObject["@@type@@"] = GetTypeName(objectType, formatter_.AssemblyFormat);
                    stack_.Peek().SetValue(jsObject);
                    stack_.Push(new StackEntry(delegate(object val) { jsObject["@@value@@"] = val; }, ""));
                }

                stack_.Peek().SetValue(converter.ConvertTo(value, typeof(string)));

                if (needTypeInfo)
                    stack_.Pop();

                return;
            }

            //iterate over members
            JavaScriptObject newJsObject = new JavaScriptObject();
            newJsObject["@@info@@"] = GetPath(stack_);
            if (needTypeInfo)
                newJsObject["@@type@@"] = GetTypeName(objectType, formatter_.AssemblyFormat);

            stack_.Peek().SetValue(newJsObject);

            MemberInfo[] members = FormatterServices.GetSerializableMembers(objectType);
            object[] memberValues = FormatterServices.GetObjectData(value, members);
            List<string> alreadySerialized = new List<string>();
            for (int i = 0; i < members.Length; ++i)
            {
                MemberInfo member = members[i];
                Type memberType = null;
                if (member is PropertyInfo)
                    memberType = (member as PropertyInfo).PropertyType;
                else if (member is FieldInfo)
                    memberType = (member as FieldInfo).FieldType;
                else if (member is EventInfo)
                    memberType = (member as EventInfo).EventHandlerType;

                string memberName = JsonFormatter.NormalizeMemberName(member.Name);
                if (!alreadySerialized.Contains(memberName))
                {
                    stack_.Push(new StackEntry(delegate(object val) { newJsObject[memberName] = val; }, "." + memberName));
                    SerializeValue(memberValues[i], memberType);
                    stack_.Pop();
                    alreadySerialized.Add(memberName);
                }
            }
        }

        private void SerializeArray(object value, Type objectType, bool needTypeInfo)
        {
            bool needLowerBoundsInfo = false;
            Array array = value as Array;
            JavaScriptArray lowerBoundsArray = new JavaScriptArray();
            for (int i = 0; i < array.Rank; ++i)
            {
                int loBound = array.GetLowerBound(i);
                if (loBound != 0)
                    needLowerBoundsInfo = true;
                lowerBoundsArray.Add(loBound);
            }

            needTypeInfo = needTypeInfo || needLowerBoundsInfo;

            if (needTypeInfo)
            {
                JavaScriptObject jsObject = new JavaScriptObject();
                stack_.Peek().SetValue(jsObject);
                jsObject["@@info@@"] = GetPath(stack_);
                jsObject["@@type@@"] = GetTypeName(objectType, formatter_.AssemblyFormat);
                if (needLowerBoundsInfo)
                    jsObject["@@lower_bounds@@"] = lowerBoundsArray;
                stack_.Push(new StackEntry(delegate(object val) { jsObject["@@value@@"] = val; }, ""));
            }

            SerializeArray(array);

            if (needTypeInfo)
                stack_.Pop();
        }

        private void SerializeDictionary(object value, Type objectType, bool needTypeInfo)
        {
            //figure out types
            Type keyType = objectType.GetGenericArguments()[0];
            Type valueType = objectType.GetGenericArguments()[1];
            Type jsonKeyValueType = typeof(JsonKeyValue<,>).MakeGenericType(new Type[] { keyType, valueType });
            Type jsonKeyValueArrayType = jsonKeyValueType.MakeArrayType();
            Type jsonKeyValueListType = typeof(List<>).MakeGenericType(new Type[] { jsonKeyValueType });
            Type keyValuePairType = typeof(KeyValuePair<,>).MakeGenericType(new Type[] { keyType, valueType });

            //get comparer
            PropertyInfo comparerProperty = objectType.GetProperty("Comparer");
            if (comparerProperty == null)
                throw new SerializationException("Could not find the Comparer property");
            object comparer = comparerProperty.GetValue(value, null);
            bool defaultComparer = comparer.GetType().FullName.StartsWith("System.Collections.Generic.GenericEqualityComparer");
            needTypeInfo = needTypeInfo || !defaultComparer;

            if (needTypeInfo)
            {
                JavaScriptObject jsObject = new JavaScriptObject();
                jsObject["@@info@@"] = GetPath(stack_);
                jsObject["@@type@@"] = GetTypeName(objectType, formatter_.AssemblyFormat);
                stack_.Peek().SetValue(jsObject);

                if (!defaultComparer)
                {
                    stack_.Push(new StackEntry(delegate(object val) { jsObject["comparer"] = val; }, ".comparer"));
                    SerializeValue(comparer, typeof(object));
                    stack_.Pop();
                }

                stack_.Push(new StackEntry(delegate(object val) { jsObject["@@value@@"] = val; }, ""));
            }

            //get enumerator
            MethodInfo getEnumeratorMethod = objectType.GetMethod("GetEnumerator");
            if (getEnumeratorMethod == null)
                throw new SerializationException("Could not find GetEnumerator method");
            IEnumerator enumerator = (IEnumerator) getEnumeratorMethod.Invoke(value, null);
            if (enumerator == null)
                throw new SerializationException("enumerator is null");

            //get key value pairs
            IList keyValuePairsList = (IList) Activator.CreateInstance(jsonKeyValueListType);
            FieldInfo keyField = jsonKeyValueType.GetField("Key");
            FieldInfo valueField = jsonKeyValueType.GetField("Value");
            PropertyInfo keyProperty = keyValuePairType.GetProperty("Key");
            PropertyInfo valueProperty = keyValuePairType.GetProperty("Value");
            while (enumerator.MoveNext())
            {
                object keyValue = enumerator.Current;
                object jsonKeyValue = Activator.CreateInstance(jsonKeyValueType);
                keyField.SetValue(jsonKeyValue, keyProperty.GetValue(keyValue, null));
                valueField.SetValue(jsonKeyValue, valueProperty.GetValue(keyValue, null));
                keyValuePairsList.Add(jsonKeyValue);
            }

            //serialize key value pairs as an array
            MethodInfo toArrayMethod = jsonKeyValueListType.GetMethod("ToArray", new Type[] { });
            if (toArrayMethod == null)
                throw new SerializationException("Could not find ToArray method of the generic list");
            object asArray = toArrayMethod.Invoke(keyValuePairsList, null);
            SerializeArray(asArray, jsonKeyValueArrayType, false);

            if (needTypeInfo)
                stack_.Pop();
        }

        private void SerializeList(object value, Type objectType, bool needTypeInfo)
        {
            if (needTypeInfo)
            {
                JavaScriptObject jsObject = new JavaScriptObject();
                jsObject["@@info@@"] = GetPath(stack_);
                jsObject["@@type@@"] = GetTypeName(objectType, formatter_.AssemblyFormat);
                stack_.Peek().SetValue(jsObject);
                stack_.Push(new StackEntry(delegate(object val) { jsObject["@@value@@"] = val; }, ""));
            }

            MethodInfo toArrayMethod = objectType.GetMethod("ToArray", new Type[] { });
            if (toArrayMethod == null)
                throw new SerializationException("Could not find ToArray method of the generic list");
            object asArray = toArrayMethod.Invoke(value, null);

            SerializeArray(asArray, asArray.GetType(), false);

            if (needTypeInfo)
                stack_.Pop();
        }

        private void SerializeElementary(object obj, bool needTypeInfo)
        {
            Type objType = (obj == null) ? typeof(object) : obj.GetType();

            if (needTypeInfo)
            {
                JavaScriptObject jsObject = new JavaScriptObject();
                jsObject["@@info@@"] = GetPath(stack_);
                jsObject["@@type@@"] = GetTypeName(objType, formatter_.AssemblyFormat);
                stack_.Peek().SetValue(jsObject);
                stack_.Push(new StackEntry(delegate(object val) { jsObject["@@value@@"] = val; }, ""));
            }

            stack_.Peek().SetValue(obj);
            /*
            if (obj == null)
            {
                stack_.Peek().SetValue(null);
            }
            else
            {
                TypeConverter converter = formatter_.GetTypeConverter(objType);
                if (converter.CanConvertTo(typeof(string)) && converter.CanConvertFrom(typeof(string)))
                    stack_.Peek().SetValue(converter.ConvertTo(obj, typeof(string)));
                else
                    throw new SerializationException("Cannot convert elementary type " + objType.FullName + " to string");
            }
             */

            if (needTypeInfo)
                stack_.Pop();
        }

        static private string GetPath(Stack<StackEntry> stack)
        {
            string path = "";
            foreach (StackEntry entry in stack)
            {
                path = entry.GetPathString() + path;
            }

            return path;
        }

        static private string GetTypeName(Type type, FormatterAssemblyStyle style)
        {
            if (style == FormatterAssemblyStyle.Full)
                return type.FullName + ", " + type.Assembly.FullName;
            else if (style == FormatterAssemblyStyle.Simple)
            {
                if (type.IsGenericType)
                {
                    Type[] arguments = type.GetGenericArguments();
                    string typeName = type.Namespace + "." + type.Name + "[";
                    for (int i = 0; i < arguments.Length; ++i)
                    {
                        if (i != 0)
                            typeName += ", ";
                        typeName += "[" + GetTypeName(arguments[i], style) + "]";
                    }

                    typeName += "], " + type.Assembly.GetName().Name;
                    return typeName;
                }
                else
                    return type.FullName + ", " + type.Assembly.GetName().Name;
            }
            else
                throw new SerializationException("Unknown FormatterAssemblyStyle: " + style);
        }
    };
}

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Runtime.Serialization;
using System.ComponentModel;
using System.Reflection;
using Newtonsoft.Json;

namespace smg.Serializers.JSON
{
    public class JsonDeserializerWorker
    {
        delegate void SetValDelegate(object val);

        class StackEntry
        {
            SetValDelegate setVal_;
            string text_;
            public StackEntry(SetValDelegate setVal, string text)
            {
                setVal_ = setVal;
                text_ = text;
            }

            public void SetValue(object val)
            {
                setVal_(val);
            }

            public string GetPathString()
            {
                return text_;
            }
        };

        JsonFormatter formatter_;
        TextReader reader_;
        object jsRoot_;
        object graph_;
        Type expectedType_;
        Stack<StackEntry> stack_ = new Stack<StackEntry>();
        Dictionary<string, object> processedValues_ = new Dictionary<string, object>();
        List<object> needDeserializationCallback_ = new List<object>();

        public JsonDeserializerWorker(JsonFormatter formatter, TextReader reader, Type expectedType)
        {
            formatter_ = formatter;
            reader_ = reader;
            expectedType_ = expectedType;
            Run();
        }

        public object GetResult()
        {
            return graph_;
        }

        private void Run()
        {
            //create json object
            using (JsonReader reader = new JsonReader(reader_))
            {
                Newtonsoft.Json.JsonSerializer serializer = new Newtonsoft.Json.JsonSerializer();
                jsRoot_ = serializer.Deserialize(reader);
            }

            stack_.Push(new StackEntry(delegate(object val) { graph_ = val; }, "root"));
            DeserializeValue(jsRoot_, expectedType_);
            stack_.Pop();

            foreach (object obj in needDeserializationCallback_)
                (obj as IDeserializationCallback).OnDeserialization(this);
        }

        private void DeserializeValue(object jsValue, Type expectedType)
        {
            //null
            if (jsValue == null)
            {
                SetStackValue(null);
                return;
            }

            //get object type
            object jsInnerValue = jsValue;
            Type objectType = expectedType;
            if (jsValue is JavaScriptObject)
            {
                JavaScriptObject jsObject = jsValue as JavaScriptObject;
                object typeName;
                if (jsObject.TryGetValue("@@type@@", out typeName))
                {
                    if (formatter_.Binder == null)
                        objectType = Type.GetType(typeName as string, true);
                    else
                    {
                        string typeNameStr = typeName as string;
                        string typeFullName = typeNameStr.Substring(0, typeNameStr.IndexOf(","));
                        string assemblyName = typeNameStr.Substring(typeNameStr.IndexOf(",")+1).Trim();
                        objectType = formatter_.Binder.BindToType(assemblyName, typeFullName);
                        if (objectType == null)
                            throw new SerializationException("Could not find the type: " + typeFullName);
                    }
                }

                if (!jsObject.TryGetValue("@@value@@", out jsInnerValue))
                    jsInnerValue = jsValue;
            }

            //root null
            if (jsInnerValue == null)
            {
                SetStackValue(null);
                return;
            }

            if (objectType == null)
                throw new SerializationException("Cannot deduce object type.  Json file probably does not specify the type of the root object.");

            //elementary values
            if (JsonFormatter.IsElementary(objectType))
            {
                DeserializeElementary(jsInnerValue, objectType);
                return;
            }

            //check for a circular reference
            if (jsValue is JavaScriptObject)
            {
                JavaScriptObject jsObject = jsValue as JavaScriptObject;
                object referencePath;
                if (jsObject.TryGetValue("@@reference@@", out referencePath))
                {
                    if (objectType.IsValueType)
                        throw new SerializationException("Invalid reference to a value type: " + (referencePath as string));

                    object processed;
                    if (!processedValues_.TryGetValue(referencePath as string, out processed))
                        throw new SerializationException("Invalid object reference: " + (referencePath as string));
                    SetStackValue(processed);
                    return;
                }
            }

            //System.Collections.Generic.Dictionary`2
            if (objectType.FullName.StartsWith("System.Collections.Generic.Dictionary`2"))
            {
                DeserializeDictionary(jsValue, jsInnerValue, objectType);
                return;
            }

            //System.Collections.Generic.List`1
            if (objectType.FullName.StartsWith("System.Collections.Generic.List`1"))
            {
                DeserializeList(jsInnerValue, objectType);
                return;
            }

            //ISerializable
            ISerializationSurrogate surrogate = formatter_.GetSurrogate(objectType);
            if (typeof(ISerializable).IsAssignableFrom(objectType) || surrogate != null)
            {
                if (!(jsValue is JavaScriptObject))
                    throw new SerializationException();

                //create uninitialized object
                object result = FormatterServices.GetUninitializedObject(objectType);
                if (!objectType.IsValueType)
                    SetStackValue(result);

                //setup serialization info
                JavaScriptObject jsObject = jsValue as JavaScriptObject;
                SerializationInfo info = new SerializationInfo(objectType, new FormatterConverter());
                foreach (string key in jsObject.Keys)
                {
                    if (key != "@@info@@" && key != "@@type@@")
                    {
                        stack_.Push(new StackEntry(delegate(object val) { info.AddValue(key, val); }, "." + key));
                        DeserializeValue(jsObject[key], typeof(object));
                        stack_.Pop();
                    }
                }

                //initialize the resulting object
                if (surrogate != null)
                    surrogate.SetObjectData(result, info, formatter_.Context, formatter_.SurrogateSelector);
                else
                {
                    Type[] ctorTypes = { typeof(SerializationInfo), typeof(StreamingContext) };
                    ConstructorInfo constructor = objectType.GetConstructor(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, 
						null, CallingConventions.HasThis, ctorTypes, null);

                    if (constructor == null)
                        throw new SerializationException("Type " + objectType.FullName + " does not have an appropriate ISerializable constructor");

                    object[] ctorParams = { info, formatter_.Context };
                    constructor.Invoke(result, ctorParams);
                }

                if (objectType.IsValueType)
                    SetStackValue(result);
                return;
            }

            //array
            if (objectType.IsArray)
            {
                DeserializeArray(jsValue, jsInnerValue, objectType);
                return;
            }

            //convert from string
            TypeConverter converter = formatter_.GetTypeConverter(objectType);
            if ((jsInnerValue is string) && converter.CanConvertFrom(typeof(string)))
            {
                SetStackValue(converter.ConvertFrom(jsInnerValue));
                return;
            }

            //do the member thing
            if (!(jsValue is JavaScriptObject))
                throw new SerializationException();
            JavaScriptObject jsValueObj = jsValue as JavaScriptObject;

            object resultObj = FormatterServices.GetUninitializedObject(objectType);
            if (!objectType.IsValueType)
                SetStackValue(resultObj);

            List<MemberInfo> members = new List<MemberInfo>();
            List<object> objects = new List<object>();
            foreach (MemberInfo member in FormatterServices.GetSerializableMembers(objectType))
            {
                string memberName = JsonFormatter.NormalizeMemberName(member.Name);

                object jsMemberObj;
                if (!jsValueObj.TryGetValue(memberName, out jsMemberObj))
                    continue;

                members.Add(member);

                Type memberType = null;
                if (member is PropertyInfo)
                    memberType = (member as PropertyInfo).PropertyType;
                else if (member is FieldInfo)
                    memberType = (member as FieldInfo).FieldType;
                else if (member is EventInfo)
                    memberType = (member as EventInfo).EventHandlerType;

                stack_.Push(new StackEntry(delegate(object val) { objects.Add(val); }, "." + memberName));
                DeserializeValue(jsMemberObj, memberType);
                stack_.Pop();
            }
            FormatterServices.PopulateObjectMembers(resultObj, members.ToArray(), objects.ToArray());

            if (objectType.IsValueType)
                SetStackValue(resultObj);
        }

        private void DeserializeArray(object jsValue, object jsInnerValue, Type objectType)
        {
            if (!(jsInnerValue is JavaScriptArray))
                throw new SerializationException("Invalid input type");

            JavaScriptArray jsArray = jsInnerValue as JavaScriptArray;
            int[] lengths = GetArrayLengths(jsArray);
            int[] lowerBounds = new int[lengths.Length];
            for (int i = 0; i < lowerBounds.Length; ++i)
                lowerBounds[i] = 0;

            //get lower bounds if specified
            if (jsValue is JavaScriptObject)
            {
                JavaScriptObject jsObj = jsValue as JavaScriptObject;
                object lowerBoundsObj;
                if (jsObj.TryGetValue("@@lower_bounds@@", out lowerBoundsObj))
                {
                    JavaScriptArray lbArray = lowerBoundsObj as JavaScriptArray;

                    for (int i = 0; i < lowerBounds.Length; ++i)
                    {
                        int idx = i;
                        stack_.Push(new StackEntry(delegate(object val) { lowerBounds[idx] = (int)val; }, "[" + i + "]"));
                        DeserializeValue(lbArray[i], typeof(int));
                        stack_.Pop();
                    }
                }
            }

            //create array
            Array result = Array.CreateInstance(objectType.GetElementType(), lengths, lowerBounds);
            SetStackValue(result);
            DeserializeArray(result, jsArray);
        }

        private void DeserializeDictionary(object jsValue, object jsInnerValue, Type objectType)
        {
            Type keyType = objectType.GetGenericArguments()[0];
            Type valueType = objectType.GetGenericArguments()[1];
            Type jsonKeyValueType = typeof(JsonKeyValue<,>).MakeGenericType(new Type[] { keyType, valueType });
            Type jsonKeyValueArrayType = jsonKeyValueType.MakeArrayType();

            //get comparer specified
            object comparerObject = null;
            if (jsValue is JavaScriptObject)
            {
                JavaScriptObject jsObj = jsValue as JavaScriptObject;
                object comparer;
                if (jsObj.TryGetValue("comparer", out comparer))
                {
                    stack_.Push(new StackEntry(delegate(object val) { comparerObject = val; }, ".comparer"));
                    DeserializeValue(comparer, typeof(object));
                    stack_.Pop();
                }
            }

            //create dictionary object
            object dictionary;
            if (comparerObject == null)
                dictionary = Activator.CreateInstance(objectType);
            else
                dictionary = Activator.CreateInstance(objectType, new object[] { comparerObject });
            SetStackValue(dictionary);

            //get keyvalue array
            object[] arrayData = null;
            stack_.Push(new StackEntry(delegate(object val) { arrayData = (object[]) val; }, "@@"));
            DeserializeArray(jsInnerValue, jsInnerValue, jsonKeyValueArrayType);
            stack_.Pop();
            if (arrayData == null)
                throw new SerializationException("arrayData is null");

            //add keyvalues to the dictionary
            MethodInfo methodAdd = objectType.GetMethod("Add");
            FieldInfo keyField = jsonKeyValueType.GetField("Key");
            FieldInfo valueField = jsonKeyValueType.GetField("Value");
            foreach (object keyValue in arrayData)
                methodAdd.Invoke(dictionary, new object[] { keyField.GetValue(keyValue), valueField.GetValue(keyValue) });
        }

        private void DeserializeList(object jsInnerValue, Type objectType)
        {
            object list = Activator.CreateInstance(objectType);
            SetStackValue(list);

            object arrayData = null;
            stack_.Push(new StackEntry(delegate(object val) { arrayData = val; }, "@@"));
            DeserializeArray(jsInnerValue, jsInnerValue, objectType.GetGenericArguments()[0].MakeArrayType());
            stack_.Pop();

            if (arrayData == null)
                throw new SerializationException("arrayData is null");

            MethodInfo method = objectType.GetMethod("AddRange");
            method.Invoke(list, new object[] { arrayData });
        }

        private void DeserializeArray(Array array, JavaScriptArray jsArray)
        {
            DeserializeArray(array, jsArray, new List<int>());
        }

        private void DeserializeArray(Array array, JavaScriptArray jsArray, List<int> indices)
        {
            if (indices.Count >= array.Rank)
                throw new SerializationException("Internal error");

            int lowerBound = array.GetLowerBound(indices.Count);
            int length = array.GetLength(indices.Count);

            for (int i = lowerBound; i < (lowerBound + length); ++i)
            {
                List<int> localIndices = new List<int>(indices);
                localIndices.Add(i);
                stack_.Push(new StackEntry(delegate(object val) { array.SetValue(val, localIndices.ToArray()); }, "[" + i + "]"));

                if (localIndices.Count < array.Rank)
                    DeserializeArray(array, jsArray[i-lowerBound] as JavaScriptArray, localIndices);
                else
                    DeserializeValue(jsArray[i-lowerBound], array.GetType().GetElementType());

                stack_.Pop();
            }
        }

        private void DeserializeElementary(object jsValue, Type objectType)
        {
            if (jsValue.GetType() == objectType)
            {
                SetStackValue(jsValue);
                return;
            }

            SetStackValue(Convert.ChangeType(jsValue, objectType));
        }

        private void SetStackValue(object value)
        {
            if (value != null)
            {
                if (value is IObjectReference)
                    value = (value as IObjectReference).GetRealObject(formatter_.Context);
                if (value is IDeserializationCallback)
                    needDeserializationCallback_.Add(value);
            }
            if (value == null || !value.GetType().IsValueType)
                processedValues_[GetPath(stack_)] = value;
            stack_.Peek().SetValue(value);
        }

        static private string GetPath(Stack<StackEntry> stack)
        {
            string path = "";
            foreach (StackEntry entry in stack)
                path = entry.GetPathString() + path;

            return path;
        }

        static private int[] GetArrayLengths(JavaScriptArray jsArray)
        {
            object jsObj = jsArray;
            List<int> lengths = new List<int>();
            while (jsObj is JavaScriptArray)
            {
                JavaScriptArray jsArrayLocal = jsObj as JavaScriptArray;
                lengths.Add(jsArrayLocal.Count);
                if (jsArrayLocal.Count == 0)
                    break;
                jsObj = jsArrayLocal[0];
            }

            return lengths.ToArray();
        }
    };
}

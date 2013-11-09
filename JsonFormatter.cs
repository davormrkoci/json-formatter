using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Runtime.Serialization;
using System.IO;
using System.ComponentModel;
using System.Runtime.Serialization.Formatters;
using Newtonsoft.Json;

namespace smg.Serializers.JSON
{
    public class JsonFormatter : IFormatter
    {
        SerializationBinder serializationBinder_;
        public SerializationBinder Binder
        {
            get { return serializationBinder_; }
            set { serializationBinder_ = value; }
        }

        StreamingContext serializationContext_ = new StreamingContext(StreamingContextStates.All);
        public StreamingContext Context
        {
            get { return serializationContext_; }
            set { serializationContext_ = value; }
        }

        ISurrogateSelector surrogateSelector_;
        public ISurrogateSelector SurrogateSelector
        {
            get { return surrogateSelector_; }
            set { surrogateSelector_ = value; }
        }

        FormatterAssemblyStyle assemblyFormat_ = FormatterAssemblyStyle.Simple;
        public FormatterAssemblyStyle AssemblyFormat
        {
            get { return assemblyFormat_; }
            set { assemblyFormat_ = value; }
        }

        Dictionary<Type, TypeConverter> customTypeConverters_;
        public Dictionary<Type, TypeConverter> CustomTypeConverters
        {
            get { return customTypeConverters_; }
            set { customTypeConverters_ = value; }
        }

        public JsonFormatter()
        {
        }

        public object Deserialize(Stream serializationStream)
        {
            return Deserialize(new StreamReader(serializationStream), null);
        }

        public object Deserialize(TextReader reader)
        {
            return Deserialize(reader, null);
        }

        public object Deserialize(Stream serializationStream, Type expectedType)
        {
            return Deserialize(new StreamReader(serializationStream), expectedType);
        }

        public object Deserialize(TextReader reader, Type expectedType)
        {
            JsonDeserializerWorker worker = new JsonDeserializerWorker(this, reader, expectedType);
            return worker.GetResult();
        }

        public void Serialize(Stream serializationStream, object graph)
        {
            Serialize(new StreamWriter(serializationStream), graph);
        }

        public void Serialize(TextWriter writer, object graph)
        {
            Serialize(writer, graph, null);
        }

        public void Serialize(Stream serializationStream, object graph, Type expectedType)
        {
            Serialize(new StreamWriter(serializationStream), graph, expectedType);
        }

        public void Serialize(TextWriter writer, object graph, Type expectedType)
        {
            JsonSerializerWorker worker = new JsonSerializerWorker(this, writer, graph, expectedType);
        }

        public static bool IsElementary(Type type)
        {
            return type.IsPrimitive || type == typeof(string);
        }

        public ISerializationSurrogate GetSurrogate(Type type)
        {
            if (surrogateSelector_ == null)
                return null;

            ISurrogateSelector selector;
            return surrogateSelector_.GetSurrogate(type, serializationContext_, out selector);
        }

        public TypeConverter GetTypeConverter(Type type)
        {
            TypeConverter converter;
            if (customTypeConverters_ != null && customTypeConverters_.TryGetValue(type, out converter))
                return converter;

            return TypeDescriptor.GetConverter(type);
        }

        const string backingFieldSubstring = ">k__BackingField";
        static int backingFieldLen = backingFieldSubstring.Length + 1;
        const string jsonFieldSubstring = "__Field";
        static int jsonFieldLen = jsonFieldSubstring.Length;
        public static string NormalizeMemberName(string name)
        {
            string[] v = name.Split(new char[] { '+' });
            string memberName = (v.Length == 1) ? v[0] : v[1];
            if (memberName.IndexOf(backingFieldSubstring) != -1 && memberName[0] == '<')
                memberName = memberName.Substring(1, memberName.Length - backingFieldLen);
            if (memberName.IndexOf(jsonFieldSubstring) != -1)
                memberName = memberName.Substring(0, memberName.Length - jsonFieldLen);

            return memberName;
        }
    }
}

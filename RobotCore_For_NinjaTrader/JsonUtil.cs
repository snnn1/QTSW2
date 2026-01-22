namespace QTSW2.Robot.Core
{
    public static class JsonUtil
    {
        public static string Serialize<T>(T obj)
        {
            try
            {
                // Try System.Web.Extensions first (works in .NET Framework and NinjaTrader)
                var serializerType = System.Type.GetType("System.Web.Script.Serialization.JavaScriptSerializer, System.Web.Extensions");
                if (serializerType != null)
                {
                    var serializer = System.Activator.CreateInstance(serializerType);
                    var maxJsonLengthProp = serializerType.GetProperty("MaxJsonLength");
                    maxJsonLengthProp?.SetValue(serializer, int.MaxValue, null);
                    var serializeMethod = serializerType.GetMethod("Serialize", new[] { typeof(object) });
                    return (string)serializeMethod!.Invoke(serializer, new object[] { obj! })!;
                }
            }
            catch
            {
                // Fall through to System.Text.Json (reflection)
            }

            // Fallback: System.Text.Json via reflection (so NinjaTrader can compile without referencing it)
            var jsonType = System.Type.GetType("System.Text.Json.JsonSerializer, System.Text.Json");
            if (jsonType != null)
            {
                var optionsType = System.Type.GetType("System.Text.Json.JsonSerializerOptions, System.Text.Json");
                if (optionsType != null)
                {
                    // Preferred overload: Serialize(object value, Type inputType, JsonSerializerOptions? options)
                    var serializeObj = jsonType.GetMethod("Serialize", new[] { typeof(object), typeof(System.Type), optionsType });
                    if (serializeObj != null)
                    {
                        return (string)serializeObj.Invoke(null, new object?[] { obj, typeof(T), null })!;
                    }
                }

                // Fallback overloads
                var serialize2 = jsonType.GetMethod("Serialize", new[] { typeof(object) });
                if (serialize2 != null)
                {
                    return (string)serialize2.Invoke(null, new object?[] { obj })!;
                }
            }

            throw new System.InvalidOperationException("No JSON serializer available (System.Web.Extensions or System.Text.Json).");
        }

        public static T Deserialize<T>(string json)
        {
            try
            {
                // Try System.Web.Extensions first (works in .NET Framework and NinjaTrader)
                var serializerType = System.Type.GetType("System.Web.Script.Serialization.JavaScriptSerializer, System.Web.Extensions");
                if (serializerType != null)
                {
                    var serializer = System.Activator.CreateInstance(serializerType);
                    var maxJsonLengthProp = serializerType.GetProperty("MaxJsonLength");
                    maxJsonLengthProp?.SetValue(serializer, int.MaxValue, null);
                    var deserializeMethod = serializerType.GetMethod("Deserialize", new[] { typeof(string), typeof(System.Type) });
                    return (T)deserializeMethod!.Invoke(serializer, new object[] { json, typeof(T) })!;
                }
            }
            catch
            {
                // Fall through to System.Text.Json (reflection)
            }

            // Fallback: System.Text.Json via reflection (so NinjaTrader can compile without referencing it)
            var jsonType = System.Type.GetType("System.Text.Json.JsonSerializer, System.Text.Json");
            if (jsonType != null)
            {
                var optionsType = System.Type.GetType("System.Text.Json.JsonSerializerOptions, System.Text.Json");
                if (optionsType != null)
                {
                    // Preferred overload: Deserialize(string json, Type returnType, JsonSerializerOptions? options)
                    var deserialize = jsonType.GetMethod("Deserialize", new[] { typeof(string), typeof(System.Type), optionsType });
                    if (deserialize != null)
                    {
                        return (T)deserialize.Invoke(null, new object?[] { json, typeof(T), null })!;
                    }
                }

                // Generic Deserialize<T>(string)
                var deserializeGeneric = jsonType.GetMethod("Deserialize", new[] { typeof(string) });
                if (deserializeGeneric != null)
                {
                    return (T)deserializeGeneric.Invoke(null, new object?[] { json })!;
                }
            }

            throw new System.InvalidOperationException("No JSON deserializer available (System.Web.Extensions or System.Text.Json).");
        }
    }
}

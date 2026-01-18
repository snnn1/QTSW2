namespace QTSW2.Robot.Core
{
    public static class JsonUtil
    {
        public static string Serialize<T>(T obj)
        {
            try
            {
                // Try System.Web.Extensions first (for .NET Framework)
                var serializerType = System.Type.GetType("System.Web.Script.Serialization.JavaScriptSerializer, System.Web.Extensions, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
                if (serializerType != null)
                {
                    var serializer = System.Activator.CreateInstance(serializerType);
                    var serializeMethod = serializerType.GetMethod("Serialize", new[] { typeof(object) });
                    return (string)serializeMethod!.Invoke(serializer, new object[] { obj! })!;
                }
            }
            catch
            {
                // Fall through to System.Text.Json
            }
            
            // Fallback to System.Text.Json (for .NET 8.0+)
            return System.Text.Json.JsonSerializer.Serialize(obj);
        }

        public static T Deserialize<T>(string json)
        {
            try
            {
                // Try System.Web.Extensions first (for .NET Framework)
                var serializerType = System.Type.GetType("System.Web.Script.Serialization.JavaScriptSerializer, System.Web.Extensions, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
                if (serializerType != null)
                {
                    var serializer = System.Activator.CreateInstance(serializerType);
                    var deserializeMethod = serializerType.GetMethod("Deserialize", new[] { typeof(string), typeof(System.Type) });
                    return (T)deserializeMethod!.Invoke(serializer, new object[] { json, typeof(T) })!;
                }
            }
            catch
            {
                // Fall through to System.Text.Json
            }
            
            // Fallback to System.Text.Json (for .NET 8.0+)
            return System.Text.Json.JsonSerializer.Deserialize<T>(json)!;
        }
    }
}

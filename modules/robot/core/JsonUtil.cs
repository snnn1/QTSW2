using System.Web.Script.Serialization;

namespace QTSW2.Robot.Core
{
    internal static class JsonUtil
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static string Serialize<T>(T obj)
        {
            return Serializer.Serialize(obj);
        }

        public static T Deserialize<T>(string json)
        {
            return Serializer.Deserialize<T>(json);
        }
    }
}

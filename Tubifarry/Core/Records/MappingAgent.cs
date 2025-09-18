namespace Tubifarry.Core.Records
{
    public record MappingAgent
    {
        public string UserAgent { get; set; } = "Tubifarry/" + PluginInfo.Version;

        public static T? MapAgent<T>(T? mappingAgent, string userAgent) where T : MappingAgent
        {
            if (mappingAgent != null)
                mappingAgent.UserAgent = userAgent;
            return mappingAgent;
        }

        public static IEnumerable<T>? MapAgent<T>(IEnumerable<T>? mappingAgent, string userAgent) where T : MappingAgent
        {
            if (mappingAgent != null)
            {
                foreach (T mapping in mappingAgent)
                    mapping.UserAgent = userAgent;
            }

            return mappingAgent;
        }

        public static List<T>? MapAgent<T>(List<T>? mappingAgent, string userAgent) where T : MappingAgent
        {
            if (mappingAgent != null)
            {
                foreach (T mapping in mappingAgent)
                    mapping.UserAgent = userAgent;
            }

            return mappingAgent;
        }
    }
}
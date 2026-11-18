using System;

namespace CustomEventByAI.AIStoryTeller
{
    public static class LlmGatewayFactory
    {
        public const string MockPrefix = "mock:";

        public static bool IsMock(string endpoint) =>
            !string.IsNullOrEmpty(endpoint) &&
            endpoint.Trim().StartsWith(MockPrefix, StringComparison.OrdinalIgnoreCase);

        public static ILlmGateway Create(string endpoint)
        {
            if (IsMock(endpoint))
            {
                string mode = endpoint.Trim().Substring(MockPrefix.Length);
                return new MockGateway(mode);
            }

            return new OpenAiCompatibleGateway();
        }
    }
}

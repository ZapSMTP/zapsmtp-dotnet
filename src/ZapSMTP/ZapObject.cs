using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapSMTP
{
    /// <summary>
    /// Base of every response model. Responses are decoded leniently: a field whose value does not have
    /// the expected type is left null instead of failing the call.
    /// </summary>
    public abstract class ZapObject
    {
        /// <summary>Fields the API sent that this version of the SDK does not know, as raw JSON.</summary>
        [JsonExtensionData]
        public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
    }

    /// <summary>Base of every request parameter type.</summary>
    public abstract class ZapParams
    {
        /// <summary>
        /// Fields to send that this version of the SDK does not know yet: they are added to the JSON body,
        /// or to the query string of a list.
        /// </summary>
        [JsonExtensionData]
        public IDictionary<string, object?>? AdditionalProperties { get; init; }
    }
}

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VLC.ScriptPlayer.Models
{
    /// <summary>
    /// Root configuration model for an interactive ECA/FSM script.
    /// </summary>
    public class ScriptConfig
    {
        [JsonProperty("version")]
        public string Version { get; set; } = "1.0";

        [JsonProperty("title")]
        public string Title { get; set; } = "Untitled Script";

        [JsonProperty("initialState")]
        public string InitialState { get; set; }

        [JsonProperty("variables")]
        public Dictionary<string, object> Variables { get; set; } = new Dictionary<string, object>();

        [JsonProperty("media")]
        public Dictionary<string, MediaItem> Media { get; set; } = new Dictionary<string, MediaItem>();

        [JsonProperty("cues")]
        public List<CuePoint> Cues { get; set; } = new List<CuePoint>();

        [JsonProperty("states")]
        public Dictionary<string, StateDef> States { get; set; } = new Dictionary<string, StateDef>();

        [JsonProperty("globalRules")]
        public List<RuleDef> GlobalRules { get; set; } = new List<RuleDef>();
    }

    /// <summary>
    /// Definition of a media asset and its playback properties.
    /// </summary>
    public class MediaItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("uri")]
        public string Uri { get; set; }

        [JsonProperty("loop")]
        public bool Loop { get; set; } = false;

        [JsonProperty("startMs")]
        public long? StartMs { get; set; }

        [JsonProperty("endMs")]
        public long? EndMs { get; set; }

        [JsonProperty("volume")]
        public double Volume { get; set; } = 1.0;
    }

    /// <summary>
    /// Represents a time-based event marker associated with a media track.
    /// </summary>
    public class CuePoint
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("mediaId")]
        public string MediaId { get; set; }

        [JsonProperty("timeMs")]
        public long TimeMs { get; set; }

        [JsonProperty("payload")]
        public string Payload { get; set; }
    }

    /// <summary>
    /// Finite state definition containing enter/exit actions and reactive rules.
    /// </summary>
    public class StateDef
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("onEnter")]
        public List<ActionDef> OnEnter { get; set; } = new List<ActionDef>();

        [JsonProperty("onExit")]
        public List<ActionDef> OnExit { get; set; } = new List<ActionDef>();

        [JsonProperty("rules")]
        public List<RuleDef> Rules { get; set; } = new List<RuleDef>();
    }

    /// <summary>
    /// Event-Condition-Action (ECA) rule mapping input or playback events to actions & state transitions.
    /// </summary>
    public class RuleDef
    {
        [JsonProperty("event")]
        public string Event { get; set; }

        [JsonProperty("conditions")]
        public List<ConditionDef> Conditions { get; set; } = new List<ConditionDef>();

        [JsonProperty("actions")]
        public List<ActionDef> Actions { get; set; } = new List<ActionDef>();

        [JsonProperty("nextState")]
        public string NextState { get; set; }
    }

    /// <summary>
    /// Predicate condition evaluated against the StateContext variable store.
    /// </summary>
    public class ConditionDef
    {
        [JsonProperty("var")]
        public string Var { get; set; }

        [JsonProperty("op")]
        public string Op { get; set; } = "==";

        [JsonProperty("val")]
        public object Val { get; set; }
    }

    /// <summary>
    /// Atomic action command dispatched upon rule trigger or state transition.
    /// </summary>
    public class ActionDef
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("params")]
        public Dictionary<string, object> Params { get; set; } = new Dictionary<string, object>();

        #region Helper Extraction Methods
        public string GetParamString(string key, string fallback = null)
        {
            if (Params != null && Params.TryGetValue(key, out var val))
            {
                if (val is JValue jv)
                {
                    return jv.Value?.ToString() ?? fallback;
                }
                if (val is JToken jt)
                {
                    return jt.ToString();
                }
                return val?.ToString() ?? fallback;
            }
            return fallback;
        }

        public long GetParamLong(string key, long fallback = 0)
        {
            if (Params != null && Params.TryGetValue(key, out var val))
            {
                if (val is JValue jv && jv.Value != null)
                {
                    try { return Convert.ToInt64(jv.Value); } catch { }
                }
                else if (val is long l) return l;
                else if (val is int i) return i;
                else if (val is double d) return (long)d;
                else if (val != null && long.TryParse(val.ToString(), out var parsed))
                {
                    return parsed;
                }
            }
            return fallback;
        }

        public double GetParamDouble(string key, double fallback = 0.0)
        {
            if (Params != null && Params.TryGetValue(key, out var val))
            {
                if (val is JValue jv && jv.Value != null)
                {
                    try { return Convert.ToDouble(jv.Value); } catch { }
                }
                else if (val is double d) return d;
                else if (val is float f) return f;
                else if (val is long l) return l;
                else if (val is int i) return i;
                else if (val != null && double.TryParse(val.ToString(), out var parsed))
                {
                    return parsed;
                }
            }
            return fallback;
        }

        public bool GetParamBool(string key, bool fallback = false)
        {
            if (Params != null && Params.TryGetValue(key, out var val))
            {
                if (val is JValue jv && jv.Value != null)
                {
                    try { return Convert.ToBoolean(jv.Value); } catch { }
                }
                else if (val is bool b) return b;
                else if (val != null && bool.TryParse(val.ToString(), out var parsed))
                {
                    return parsed;
                }
            }
            return fallback;
        }
        #endregion
    }
}

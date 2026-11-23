using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using VLC.ScriptPlayer.Models;

namespace VLC.ScriptPlayer.Engine
{
    /// <summary>
    /// Evaluates ECA rule triggers and conditions against current StateContext variables.
    /// </summary>
    public static class RuleEvaluator
    {
        public static bool MatchesEvent(RuleDef rule, string incomingEvent)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Event))
            {
                return false;
            }

            if (rule.Event == "*" || string.Equals(rule.Event, incomingEvent, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        public static bool EvaluateConditions(List<ConditionDef> conditions, StateContext context)
        {
            if (conditions == null || conditions.Count == 0)
            {
                return true;
            }

            foreach (var cond in conditions)
            {
                if (!EvaluateCondition(cond, context))
                {
                    return false;
                }
            }

            return true;
        }

        public static bool EvaluateCondition(ConditionDef condition, StateContext context)
        {
            if (condition == null || string.IsNullOrWhiteSpace(condition.Var))
            {
                return true;
            }

            var currentVal = ExtractValue(context.GetVariable(condition.Var));
            var expectedVal = ExtractValue(condition.Val);
            var op = (condition.Op ?? "==").Trim().ToLowerInvariant();

            switch (op)
            {
                case "truthy":
                    return IsTruthy(currentVal);

                case "falsy":
                    return !IsTruthy(currentVal);

                case "==":
                case "eq":
                    return AreEqual(currentVal, expectedVal);

                case "!=":
                case "neq":
                    return !AreEqual(currentVal, expectedVal);

                case ">":
                case "gt":
                    return CompareNumbers(currentVal, expectedVal) > 0;

                case ">=":
                case "gte":
                    return CompareNumbers(currentVal, expectedVal) >= 0;

                case "<":
                case "lt":
                    return CompareNumbers(currentVal, expectedVal) < 0;

                case "<=":
                case "lte":
                    return CompareNumbers(currentVal, expectedVal) <= 0;

                case "contains":
                    var currStr = currentVal?.ToString() ?? "";
                    var expStr = expectedVal?.ToString() ?? "";
                    return currStr.IndexOf(expStr, StringComparison.OrdinalIgnoreCase) >= 0;

                default:
                    context.AddLog($"[WARN] Unknown condition operator: {op}");
                    return false;
            }
        }

        private static bool IsTruthy(object val)
        {
            if (val == null) return false;
            if (val is bool b) return b;
            if (val is int i) return i != 0;
            if (val is long l) return l != 0;
            if (val is double d) return Math.Abs(d) > 0.0000001;
            if (val is string s) return !string.IsNullOrWhiteSpace(s) && !s.Equals("false", StringComparison.OrdinalIgnoreCase) && s != "0";
            return true;
        }

        private static bool AreEqual(object left, object right)
        {
            if (left == null && right == null) return true;
            if (left == null || right == null) return false;

            // Try number comparison
            if (TryExtractDouble(left, out var lNum) && TryExtractDouble(right, out var rNum))
            {
                return Math.Abs(lNum - rNum) < 0.0001;
            }

            // Boolean comparison
            if (left is bool lb && TryExtractBool(right, out var rb))
            {
                return lb == rb;
            }

            // String fallback
            return string.Equals(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareNumbers(object left, object right)
        {
            if (!TryExtractDouble(left, out var lVal)) lVal = 0.0;
            if (!TryExtractDouble(right, out var rVal)) rVal = 0.0;
            return lVal.CompareTo(rVal);
        }

        private static bool TryExtractDouble(object obj, out double val)
        {
            val = 0.0;
            if (obj == null) return false;
            if (obj is double d) { val = d; return true; }
            if (obj is int i) { val = i; return true; }
            if (obj is long l) { val = l; return true; }
            if (obj is float f) { val = f; return true; }
            return double.TryParse(obj.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out val);
        }

        private static bool TryExtractBool(object obj, out bool val)
        {
            val = false;
            if (obj == null) return false;
            if (obj is bool b) { val = b; return true; }
            return bool.TryParse(obj.ToString(), out val);
        }

        public static object ExtractValue(object raw)
        {
            if (raw == null) return null;
            if (raw is JValue jv)
            {
                return jv.Value;
            }
            if (raw is JToken jt)
            {
                return jt.ToString();
            }
            return raw;
        }
    }
}

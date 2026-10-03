using System;
using UnityEngine;
using static Ami.BroAudio.PlaybackGroup;

namespace Ami.BroAudio
{
    /// <summary>
    /// Stores the value required for rule execution.
    /// </summary>
    /// <remarks>
    /// Note that the behavior needs to be specified in the <see cref="PlaybackGroup.InitializeRules"/> method using <see cref="Initialize"/>.
    /// </remarks>
    /// <typeparam name="T">The type of the rule value</typeparam>
    [Serializable]
    public abstract class Rule<T> : IRule
    {
        public T Value;
        private IsPlayableDelegate _ruleMethod;
        [SerializeField] private bool _isOverride = true;
        public override string ToString() => base.ToString().Remove(0, "Ami.BroAudio.".Length) + " | Value: " + Value.ToString();
        
        public IsPlayableDelegate RuleMethod
        {
            get
            {
                if(_ruleMethod == null)
                {
                    Debug.LogError(Utility.LogTitle + $"{GetType()} is not initialized yet! As a result, a method that always passes will be returned.");
                    return RuleExtension.EmptyRuleMethod;;
                }
                return _ruleMethod;
            }
        }

        public Rule(T value)
        {
            Value = value;
        }

        /// <summary>
        /// Sets the rule to be executed based on the override status
        /// </summary>
        /// <param name="ruleMethod"></param>
        /// <param name="onGetParentRule"></param>
        /// <returns></returns>
        internal IRule Initialize(IsPlayableDelegate ruleMethod, Func<Type, IRule> onGetParentRule)
        {
            if (_isOverride)
            {
                _ruleMethod = ruleMethod;
                _ruleMethod ??= RuleExtension.EmptyRuleMethod;;
                return this;
            }

            var parentRule = onGetParentRule?.Invoke(GetType());
            _ruleMethod = parentRule?.RuleMethod;
            _ruleMethod ??= RuleExtension.EmptyRuleMethod;;
            return this;
        }

        public static implicit operator T(Rule<T> property) => property == null ? default : property.Value;

        public static class NameOf
        {
            public const string IsOverride = nameof(_isOverride);
        }
    }

    public class EmptyRule : IRule
    {
        public EmptyRule(Type ruleType)
        {
            Debug.LogError(Utility.LogTitle + $"Can't find a valid rule instance of {ruleType}, It might not be initialized, or there's no default rule available when the override option is off. As a result, a method that always passes is returned");
        }

        public IsPlayableDelegate RuleMethod => RuleExtension.EmptyRuleMethod;
    }

    public static class RuleExtension
    {
        public static IsPlayableDelegate EmptyRuleMethod = (x,y) => true;
    }
}
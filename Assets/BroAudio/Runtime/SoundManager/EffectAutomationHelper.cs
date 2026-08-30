using System;
using System.Collections;
using System.Collections.Generic;
using Ami.Extension;
using UnityEngine;
using UnityEngine.Audio;
using Ami.BroAudio.Tools;

namespace Ami.BroAudio.Runtime
{
    public class EffectAutomationHelper : CoroutineBehaviour,IAutoResetWaitable
    {
        private class Tweaker
        {
            public bool IsTweaking;
            public Coroutine Coroutine;
            // The latest Effect and its callback: the dict key is only the EffectType, and both
            // GetEffectParameterName and a restart from DecorateTweakingWaitable need the rest.
            public Effect Effect;
            public Action<EffectType> OnReset;
            // The list is very small, there's no need to use other collection type.
            public List<ITweakingWaitable> WaitableList;
        }

        public interface ITweakingWaitable : IComparable<ITweakingWaitable>
        {
            Effect Effect { get; }
            bool IsFinished();
            IEnumerator GetYieldInstruction();
        }

        private class TweakingWaitableBase : ITweakingWaitable
        {
            public Effect Effect { get; set; }
            public TweakingWaitableBase(Effect effect) => Effect = effect;
            public IEnumerator GetYieldInstruction() => null;
            public bool IsFinished() => false;

            public int CompareTo(ITweakingWaitable other) => Effect.CompareTo(other.Effect);
        }

        private abstract class TweakingWaitableDecorator : ITweakingWaitable
        {
            protected ITweakingWaitable Base;
            public void AttachTo(ITweakingWaitable waitable) => Base = waitable;

            public Effect Effect => Base.Effect; 
            public abstract IEnumerator GetYieldInstruction();
            public abstract bool IsFinished();

            public int CompareTo(ITweakingWaitable other) => Base.Effect.CompareTo(other.Effect);
        }

        private class TweakAndWaitSeconds : TweakingWaitableDecorator
        {
            public readonly float EndTime;
            private WaitUntil _waitUntil = null;

            public TweakAndWaitSeconds(float seconds)
            {
                EndTime = Time.time + seconds;
            }

            public override bool IsFinished() => Time.time >= EndTime;

            public override IEnumerator GetYieldInstruction()
            {
                _waitUntil ??= new WaitUntil(IsFinished);
                yield return _waitUntil;
            }
        }

        private class TweakAndWaitUntil : TweakingWaitableDecorator
        {
            public readonly IEnumerator Enumerator;
            public readonly Func<bool> Condition;
            private readonly bool _invertCondition;

            public TweakAndWaitUntil(IEnumerator enumerator, Func<bool> condition, bool invertCondition = false)
            {
                Enumerator = enumerator;
                Condition = condition;
                _invertCondition = invertCondition;
            }

            public override IEnumerator GetYieldInstruction() => Enumerator;
            public override bool IsFinished() => _invertCondition ? !Condition() : Condition();
        }

        private readonly AudioMixer _mixer = null;
        private Dictionary<EffectType,Tweaker> _tweakerDict = new Dictionary<EffectType, Tweaker>();
        private EffectType _latestEffect = default;


        public EffectAutomationHelper(MonoBehaviour mono, AudioMixer mixer) : base(mono)
        {
            _mixer = mixer;
        }

        public WaitForSeconds ForSeconds(float seconds)
        {
            var decoration = new TweakAndWaitSeconds(seconds);
            DecorateTweakingWaitable(decoration);
            return new WaitForSeconds(seconds);
        }

        public WaitUntil Until(Func<bool> condition)
        {
            var instruction = new WaitUntil(condition);
            var decoration = new TweakAndWaitUntil(instruction,condition);
            DecorateTweakingWaitable(decoration);
            return instruction;
        }

        public WaitWhile While(Func<bool> condition)
        {
            var instruction = new WaitWhile(condition);
            var decoration = new TweakAndWaitUntil(instruction, condition, true);
            DecorateTweakingWaitable(decoration);
            return instruction;
        }


        private void DecorateTweakingWaitable(TweakingWaitableDecorator decoration)
        {
            // this should be called after the first tweak is started, the purpose of decorating is to know when will it stop.
            if (_latestEffect == EffectType.None)
            {
                Debug.LogWarning(Utility.LogTitle + $"AutoResetWaitable on {_latestEffect} is not supported.");
            }
            else if (_tweakerDict.TryGetValue(_latestEffect, out var tweaker))
            {
                tweaker.WaitableList ??= new List<ITweakingWaitable>();
                if (tweaker.WaitableList.Count == 0)
                {
                    // Nothing left to decorate: a zero fadeTime makes Tweak yield nothing, so StartCoroutine
                    // already drained this list before SetEffect returned. Re-queue, decorated up front so the
                    // restart can't drain it too - doing nothing would drop the auto-reset this waitable is for.
                    decoration.AttachTo(new TweakingWaitableBase(tweaker.Effect));
                    tweaker.WaitableList.Add(decoration);
                    RestartCoroutine(TweakTrackParameter(tweaker, tweaker.Effect.Type, tweaker.Effect.IsDominator, tweaker.OnReset), ref tweaker.Coroutine);
                    return;
                }

                int lastIndex = tweaker.WaitableList.Count - 1;
                var current = tweaker.WaitableList[lastIndex];
                if(current is TweakingWaitableBase)
                {
                    decoration.AttachTo(current);
                    tweaker.WaitableList[lastIndex] = decoration;
                }
                else
                {
                    Debug.LogError(Utility.LogTitle + $"The latest waitable isn't the base type:{nameof(TweakingWaitableBase)}");
                }
            }
        }

        public void SetEffectTrackParameter(Effect effect, Action<EffectType> onReset)
        {
            _latestEffect = effect.Type;
            if (_latestEffect == EffectType.None)
            {
                ResetAllEffect(effect, onReset);
                return;
            }

            if (!_tweakerDict.TryGetValue(effect.Type, out var tweaker))
            {
                tweaker = new Tweaker();
                _tweakerDict.Add(effect.Type, tweaker);
            }
            tweaker.Effect = effect;
            tweaker.OnReset = onReset;

            bool isNullOrEmpty = tweaker.WaitableList == null || tweaker.WaitableList.Count == 0;
            bool isMoreIntense = !isNullOrEmpty && effect.IsMoreIntenseThan(tweaker.WaitableList[tweaker.WaitableList.Count - 1].Effect);
            tweaker.WaitableList ??= new List<ITweakingWaitable>();
            tweaker.WaitableList.Add(new TweakingWaitableBase(effect));
            tweaker.WaitableList.Sort();

            if (!tweaker.IsTweaking || isMoreIntense)
            {
                RestartCoroutine(TweakTrackParameter(tweaker, effect.Type, effect.IsDominator, onReset), ref tweaker.Coroutine);
                if (effect.IsDominator)
                {
                    SwitchMainTrackMode(true);
                }
            }
        }

        private void SwitchMainTrackMode(bool isDominatorActive)
        {
            string from = isDominatorActive ? BroName.MainTrackName : BroName.MainDominatedTrackName;
            string to = isDominatorActive ? BroName.MainDominatedTrackName : BroName.MainTrackName;

            _mixer.ChangeChannel(from, to, AudioConstant.FullDecibelVolume);
        }

        private IEnumerator TweakTrackParameter(Tweaker tweaker, EffectType resetType, bool isDominator, Action<EffectType> onReset)
        {
            tweaker.IsTweaking = true;
            while (tweaker.WaitableList.Count > 0)
            {
                int lastIndex = tweaker.WaitableList.Count - 1;
                var effect = tweaker.WaitableList[lastIndex].Effect;
                string paraName = GetEffectParameterName(effect, out bool hasSecondaryParameter);
                float currentValue = GetCurrentValue(effect);

                yield return Tweak(currentValue, effect.Value, effect.Fading.FadeIn, effect.Fading.FadeInEase, paraName, hasSecondaryParameter);
                // if there's waitable, it should be decorated after this tweak

                var waitable = tweaker.WaitableList[lastIndex];
                IEnumerator yieldInstruction = waitable.GetYieldInstruction();

                if(yieldInstruction != null)
                {
                    if (!waitable.IsFinished())
                    {
                        yield return yieldInstruction;
                    }

                    if (tweaker.WaitableList.Count == 1)
                    {
                        // auto reset to origin after last waitable 
                        yield return Tweak(GetCurrentValue(effect), GetEffectDefaultValue(effect.Type), effect.Fading.FadeOut, effect.Fading.FadeOutEase, paraName, hasSecondaryParameter);
                    }
                }
                tweaker.WaitableList.RemoveAt(lastIndex);
            }
            tweaker.IsTweaking = false;
            onReset?.Invoke(resetType);
            if (isDominator)
            {
                SwitchMainTrackMode(false);
            }
        }

        private IEnumerator Tweak(float from, float to, float fadeTime, Ease ease, string paraName,bool hasSecondaryParameter = false, Action onTweakingFinshed = null)
        {
            if(from == to)
            {
                yield break;
            }

            string secondaryParaName = hasSecondaryParameter ? paraName + "2" : null;
            float currentTime = 0f;
            while (currentTime < fadeTime)
            {
                currentTime += Utility.GetDeltaTime();
                float value = Mathf.Lerp(from, to, (currentTime / fadeTime).SetEase(ease));
                _mixer.SafeSetFloat(paraName, value);
                _mixer.SafeSetFloat(secondaryParaName, value);
                yield return null;
            }

            _mixer.SafeSetFloat(paraName, to);
            _mixer.SafeSetFloat(secondaryParaName, to);
            onTweakingFinshed?.Invoke();
        }

        private bool TryGetCurrentValue(Effect effect, out float value)
        {
            string paraName = GetEffectParameterName(effect, out bool _);
            if (!_mixer.SafeGetFloat(paraName, out value))
            {
                Debug.LogError(Utility.LogTitle + $"Can't get exposed parameter of {effect.Type}");
                return false;
            }
            return true;
        }

        private float GetCurrentValue(Effect effect)
        {
            if(TryGetCurrentValue(effect , out var result))
            {
                return result;
            }
            return default;
        }

        private void ResetAllEffect(Effect effect, Action<EffectType> onResetFinished)
        {
            // Starts at 1 for the loop itself, released below. Tweak can finish inside StartCoroutine
            // (nothing to fade, or no fade time), which would otherwise end the reset on the first effect.
            int tweakingCount = 1;
            Action onTweakFinished = OnTweakingFinished;
            foreach (var pair in _tweakerDict)
            {
                Tweaker tweaker = pair.Value;
                EffectType effectType = pair.Key;
                // The incoming effect is always EffectType.None and only carries the Fading to reset with;
                // the mixer parameter has to be resolved from the tracked entry.
                if (TryGetCurrentValue(tweaker.Effect, out float current))
                {
                    string paraName = GetEffectParameterName(tweaker.Effect, out bool hasSecondaryParameter);
                    SafeStopCoroutine(tweaker.Coroutine);
                    tweakingCount++;
                    tweaker.Coroutine = StartCoroutine(Tweak(current, GetEffectDefaultValue(effectType), effect.Fading.FadeOut, effect.Fading.FadeOutEase, paraName, hasSecondaryParameter, onTweakFinished));
                    tweaker.WaitableList.Clear();
                }
            }
            OnTweakingFinished();

            void OnTweakingFinished()
            {
                tweakingCount--;
                if(tweakingCount <= 0)
                {
                    onResetFinished?.Invoke(EffectType.All);
                }
            }
        }

        private string GetEffectParameterName(Effect effect, out bool hasSecondaryParameter)
        {
            hasSecondaryParameter = false;
            switch (effect.Type)
            {
                case EffectType.Volume:
                    if(effect.IsDominator)
                    {
                        return BroName.MainDominatedTrackName;
                    }
                    else
                    {
                        Debug.LogError(Utility.LogTitle + $"{effect.Type} is only supported on Dominator");
                        return string.Empty;
                    }
                case EffectType.LowPass:
                    hasSecondaryParameter = SoundManager.Instance.Setting.AudioFilterSlope == FilterSlope.FourPole;
                    return effect.IsDominator ? BroName.Dominator_LowPassParaName : BroName.LowPassParaName;
                case EffectType.HighPass:
                    hasSecondaryParameter = SoundManager.Instance.Setting.AudioFilterSlope == FilterSlope.FourPole;
                    return effect.IsDominator ? BroName.Dominator_HighPassParaName : BroName.HighPassParaName;
                case EffectType.Custom:
                    return effect.CustomExposedParameter;
                default:
                    return string.Empty;
            }
        }

        private float GetEffectDefaultValue(EffectType effectType) => effectType switch
        {
            EffectType.Volume => AudioConstant.FullDecibelVolume,
            EffectType.LowPass => AudioConstant.MaxFrequency,
            EffectType.HighPass => AudioConstant.MinFrequency,
            _ => -1f,
        };
    }
}

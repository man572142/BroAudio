using System;
using Ami.Extension;
using static UnityEngine.Debug;

namespace Ami.BroAudio
{
    /// <summary>
    /// Parameters for setting effects. Please use the static factory methods within this class.
    /// </summary>
    [System.Serializable]
    public struct Effect : IComparable<Effect>
    {
        // Use these static methods for SetEffect()
        public static Effect HighPass(float frequency, float fadeTime = 0f, Ease ease = BroAdvice.HighPassInEase) 
            => new Effect(EffectType.HighPass, frequency, SingleFading(fadeTime, ease));
        public static Effect ResetHighPass(float fadeTime = 0f, Ease ease = BroAdvice.HighPassOutEase) 
            => new Effect(EffectType.HighPass, AudioConstant.MinFrequency, SingleFading(fadeTime, ease));
        public static Effect LowPass(float frequency, float fadeTime = 0f, Ease ease = BroAdvice.LowPassInEase) 
            => new Effect(EffectType.LowPass, frequency, SingleFading(fadeTime, ease));
        public static Effect ResetLowPass(float fadeTime = 0f, Ease ease = BroAdvice.LowPassOutEase) 
            => new Effect(EffectType.LowPass, AudioConstant.MaxFrequency, SingleFading(fadeTime, ease));
        public static Effect Custom(string exposedParameterName, float value, float fadeTime = 0f, Ease ease = Ease.Linear) 
            => new Effect(exposedParameterName, value, SingleFading(fadeTime, ease));
        public static class Defaults
        {
            public const float Volume = AudioConstant.FullVolume;
            public const float LowPass = AudioConstant.MaxFrequency;
            public const float HighPass = AudioConstant.MinFrequency;
        }

        private static Fading SingleFading(float fadeTime, Ease ease) => new Fading(fadeTime, default, ease, default);


        private float _value;

        public readonly EffectType Type;
        public readonly Fading Fading;
        public readonly string CustomExposedParameter;
        internal readonly bool IsDominator;

        // Force user to use static factory method
        internal Effect(EffectType type, float value, Fading fading, bool isDominator = false) : this(type)
        {
            Value = value;
            Fading = fading;
            IsDominator = isDominator;
        }

        internal Effect(string exposedParaName, float value, Fading fading) : this(EffectType.Custom, value, fading)
        {
            CustomExposedParameter = exposedParaName;
        }

        public Effect(EffectType type) : this()
        {
            Type = type;

            Value = type switch
            {
                EffectType.Volume => AudioConstant.FullVolume,
                EffectType.LowPass => BroAdvice.LowPassFrequency,
                EffectType.HighPass => BroAdvice.HighPassFrequency,
                _ => default,
            };
        }

        public float Value
        {
            get => _value;
            private set
            {
                switch (Type)
                {
                    case EffectType.None:
                        LogError(Utility.LogTitle + "EffectParameter's EffectType must be set before the Value");
                        break;
                    case EffectType.Volume:
                        _value = value.ToDecibel();
                        break;
                    case EffectType.LowPass:
                    case EffectType.HighPass:
                        if (AudioExtension.IsValidFrequency(value))
                        {
                            _value = value;
                        }
                        break;
                    default:
                        _value = value; 
                        break;
                }
            }
        }

        // Default means neutral (no audible effect), not "as constructed": Effect(EffectType) seeds an audible BroAdvice value.
        public bool IsDefault() => Type switch
        {
            EffectType.Volume => Value == AudioConstant.FullDecibelVolume,
            EffectType.LowPass => Value == Defaults.LowPass,
            EffectType.HighPass => Value == Defaults.HighPass,
            _ => false,
        };

        public int CompareTo(Effect other)
        {
            if (Type != other.Type)
            {
                return ((int)Type).CompareTo((int)other.Type);
            }

            switch (Type)
            {
                case EffectType.Volume:
                case EffectType.HighPass:
                    return Value.CompareTo(other.Value);
                case EffectType.LowPass:
                    return Value.CompareTo(other.Value) * -1;
            }
            return 0;
        }
    }

    public static class EffectExtension
    {
        public static bool IsMoreIntenseThan(this Effect x, Effect y)
        {
            return x.CompareTo(y) > 0;
        }
    }
}
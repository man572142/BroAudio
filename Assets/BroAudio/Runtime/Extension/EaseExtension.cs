using UnityEngine;

namespace Ami.Extension
{
    public static class EaseExtension
    {
        public static float SetEase(this float value, Ease ease)
        {
            value = Mathf.Clamp01(value);

            return ease switch
            {
                Ease.Linear => value,
                Ease.InQuad => Mathf.Pow(value, 2),
                Ease.InCubic => Mathf.Pow(value, 3),
                Ease.InQuart => Mathf.Pow(value, 4),
                Ease.InQuint => Mathf.Pow(value, 5),
                Ease.InSine => 1 - Mathf.Cos((value * Mathf.PI) / 2),
                Ease.InCirc => 1 - Mathf.Sqrt(1 - Mathf.Pow(value, 2)),
                Ease.OutQuad => 1 - Mathf.Pow((1 - value), 2),
                Ease.OutCubic => 1 - Mathf.Pow((1 - value), 3),
                Ease.OutQuart => 1 - Mathf.Pow((1 - value), 4),
                Ease.OutQuint => 1 - Mathf.Pow((1 - value), 5),
                Ease.OutSine => Mathf.Sin((value * Mathf.PI) / 2),
                Ease.OutCirc => Mathf.Sqrt(1 - Mathf.Pow(value - 1, 2)),
                Ease.InOutQuad => value < 0.5f ? 2 * Mathf.Pow(value, 2) : 1 - Mathf.Pow(-2 * value + 2, 2) / 2,
                Ease.InOutCubic => value < 0.5f ? 2 * Mathf.Pow(value, 3) : 1 - Mathf.Pow(-2 * value + 2, 3) / 2,
                Ease.InOutQuart => value < 0.5f ? 2 * Mathf.Pow(value, 4) : 1 - Mathf.Pow(-2 * value + 2, 4) / 2,
                Ease.InOutQuint => value < 0.5f ? 2 * Mathf.Pow(value, 5) : 1 - Mathf.Pow(-2 * value + 2, 5) / 2,
                Ease.InOutSine => -(Mathf.Cos(Mathf.PI * value) - 1) / 2,
                Ease.InOutCirc => value < 0.5 ?
                                        (1 - Mathf.Sqrt(1 - Mathf.Pow(2 * value, 2))) / 2 : (Mathf.Sqrt(1 - Mathf.Pow(-2 * value + 2, 2)) + 1) / 2,
                _ => 0,
            };
        }
    } 

    public enum Ease
    {
	    Linear,

	    InQuad,
	    InCubic,
	    InQuart,
	    InQuint,
	    InSine,
	    InCirc,

	    OutQuad,
	    OutCubic,
	    OutQuart,
	    OutQuint,
	    OutSine,
	    OutCirc,

	    InOutQuad,
	    InOutCubic,
	    InOutQuart,
	    InOutQuint,
	    InOutSine,
	    InOutCirc,
    }
}
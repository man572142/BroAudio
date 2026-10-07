using System;
using UnityEngine;

namespace Ami.BroAudio.Editor 
{
	public class Transport : ITransport
	{
		public const int FloatFieldDigits = 3;

		public virtual float StartPosition => PlaybackValues[0];
		public virtual float EndPosition => PlaybackValues[1];
		public virtual float Delay => PlaybackValues[2];
		public virtual float FadeIn => FadingValues[0];
		public virtual float FadeOut => FadingValues[1];
		public float FullLength { get; private set; }
		public float[] PlaybackValues { get; private set; }
		public float[] FadingValues { get; private set; }

		public Transport(float length)
		{
			FullLength = length;
			PlaybackValues = new float[3]; // StartPosition, EndPosition, Delay
			FadingValues = new float[2]; // FadeIn, FadeOut
		}

		// Delay counts: the clip editor saves it as silence prepended to the clip.
		public bool HasDifferentPosition => StartPosition != 0f || EndPosition != 0f || (Delay > StartPosition);
		public bool HasFading => FadeIn != 0f || FadeOut != 0f;

		public virtual void SetValue(float newValue, TransportType transportType)
		{
			switch (transportType)
			{
				case TransportType.Start:
					PlaybackValues[0] = ClampAndRound(newValue, transportType);
					break;
				case TransportType.End:
					PlaybackValues[1] = ClampAndRound(newValue, transportType);
					break;
				case TransportType.Delay:
					PlaybackValues[2] = Mathf.Max(newValue,0f);
					break;
				case TransportType.FadeIn:
					FadingValues[0] = ClampAndRound(newValue, transportType);
					break;
				case TransportType.FadeOut:
					FadingValues[1] = ClampAndRound(newValue, transportType);
					break;
			}
		}

		public void Update()
		{
			PlaybackValues[0] = StartPosition;
			PlaybackValues[1] = EndPosition;
			PlaybackValues[2] = Delay;
			FadingValues[0] = FadeIn;
			FadingValues[1] = FadeOut;
		}

		private float ClampAndRound(float value, TransportType transportType)
		{
			float clamped = Mathf.Clamp(value, 0f, GetLengthLimit(transportType));
			return (float)Math.Round(clamped, FloatFieldDigits, MidpointRounding.AwayFromZero);
		}

		private float GetLengthLimit(TransportType modifiedType)
		{
			return FullLength - GetLength(TransportType.Start, StartPosition) - GetLength(TransportType.End, EndPosition) - GetLength(TransportType.FadeIn, FadeIn) - GetLength(TransportType.FadeOut, FadeOut);

			float GetLength(TransportType transportType, float value)
			{
				return modifiedType != transportType ? value : 0f;
			}
		}
	}
}
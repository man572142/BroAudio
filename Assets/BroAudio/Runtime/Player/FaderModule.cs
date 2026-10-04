using Ami.Extension;
using System;
using System.Collections;
using UnityEngine;

namespace Ami.BroAudio.Runtime
{
    /// <summary>
    /// Simulating a mixer's fader
    /// </summary>
    public class Fader
    {
        private readonly IAudioBus _bus;
        private float _origin;
        private Coroutine _coroutine;

        private float _elapsedTime;
        private float _fadeTime;
        private Ease _ease;

        public float Current { get; private set; }
        public float Target { get; private set; }
        public bool IsFading => IsFadingIn || IsFadingOut;
        public bool IsFadingIn => _coroutine != null && Current < Target;
        public bool IsFadingOut => _coroutine != null && Current > Target;

        public float RemainingTime => Mathf.Max(0f, _fadeTime - _elapsedTime);
        public Ease Ease => _ease;

        private MonoBehaviour _coroutineExecutor => SoundManager.Instance;

        public Fader(float value, IAudioBus bus)
        {
            _bus = bus;
            _origin = value;
            Target = value;
            Current = value;
        }

        public void SetTarget(float value)
        {
            _origin = Current;
            Target = value;
        }

        public void Fade(float fadeTime, Ease ease)
        {
            BeginFade(fadeTime, ease);
            RestartCoroutine(FadeLoop());
        }

        public void Fade<T>(float fadeTime, Ease ease, Action<T> onUpdate, T state)
        {
            BeginFade(fadeTime, ease);
            RestartCoroutine(FadeLoop(onUpdate, state));
        }

        private IEnumerator FadeLoop()
        {
            while (Update())
            {
                yield return null;
            }
        }

        private IEnumerator FadeLoop<T>(Action<T> onUpdate, T state)
        {
            while (Update()) 
            { 
                yield return null;
                onUpdate?.Invoke(state);      
            }
        }

        private void BeginFade(float fadeTime, Ease ease)
        {
            _elapsedTime = 0f;
            _fadeTime = fadeTime;
            _ease = ease;
        }

        public void Resume(float current, float target)
        {
            Current = current;
            Target = target;
            _origin = current;
        }

        public void Complete(float value, bool updateBus = true)
        {
            StopCoroutine();
            Current = value;
            Target = value;
            _elapsedTime = 0f;
            _fadeTime = 0f;
            _ease = default;
            if(updateBus)
            {
                _bus.UpdateVolume();
            }
        }

        public bool Update()
        {
            if(_fadeTime <= 0f || Mathf.Approximately(_origin, Target))
            {
                Current = Target;
                return false;
            }

            if(_elapsedTime < _fadeTime)
            {
                Current = Mathf.Lerp(_origin, Target, (_elapsedTime / _fadeTime).SetEase(_ease));
                _bus.UpdateVolume();

                _elapsedTime += Utility.GetDeltaTime();
                return true;
            }

            Complete(Target);
            return false;
        }

        private void StopCoroutine()
        {
            // Reachable during teardown, so it must not touch the throwing SoundManager.Instance.
            MonoBehaviour executor = SoundManager.HasInstance ? _coroutineExecutor : null;
            executor.SafeStopCoroutine(_coroutine);
            _coroutine = null;
        }

        private void RestartCoroutine(IEnumerator enumerator)
        {
            _coroutineExecutor.RestartCoroutine(enumerator, ref _coroutine);
        }
    }
}

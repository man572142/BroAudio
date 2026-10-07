using System;
using System.Collections.Generic;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using UnityEngine;

namespace Ami.BroAudio
{
    /// <summary>
    /// To keep tracking the instance of an AudioPlayer
    /// </summary>
    public class AudioPlayerInstanceWrapper : InstanceWrapper<AudioPlayer>, IAudioPlayer, IMusicPlayer, IPlayerEffect
    {
        public AudioPlayerInstanceWrapper(AudioPlayer instance) : base(instance)
        {
        }

        protected override void LogInstanceIsNull()
        {
            if (SoundManager.HasInstance && SoundManager.Instance.Setting.LogAccessRecycledPlayerWarning)
            {
                Debug.LogWarning(Utility.LogTitle + "This audio player has been recycled after playback and can no longer be accessed. \n" +
                    "To avoid this warning, check IsActive before use, or use the OnEnd callback to clear your IAudioPlayer reference when playback completes.");
            }
        }

        #region Interface
#pragma warning disable UNT0008
        public SoundID ID => IsAvailable() ? Instance.ID : SoundID.Invalid;
        public bool IsActive => IsAvailable(false) && Instance.IsActive;
        public bool IsPlaying => IsAvailable(false) && Instance.IsPlaying;
        public IBroAudioClip CurrentPlayingClip => Instance?.CurrentPlayingClip;
        IMusicPlayer IMusicDecoratable.AsBGM() => IsAvailable() ? Wrap(Instance.AsBGM()) : Empty.MusicPlayer;
#if !UNITY_WEBGL
        IPlayerEffect IEffectDecoratable.AsDominator() => IsAvailable() ? Wrap(Instance.AsDominator()) : Empty.DominatorPlayer;
#endif
        IAudioPlayer IVolumeSettable.SetVolume(float vol, float fadeTime) => IsAvailable() ? Wrap(Instance.SetVolume(vol, fadeTime)) : Empty.AudioPlayer;
        IAudioPlayer IAudioPlayer.SetPitch(float pitch, float fadeTime) => IsAvailable() ? Wrap(Instance.SetPitch(pitch, fadeTime)) : Empty.AudioPlayer;
        IAudioPlayer IAudioPlayer.SetVelocity(int velocity) => IsAvailable() ? Wrap(Instance.SetVelocity(velocity)) : Empty.AudioPlayer;
        IAudioPlayer IAudioPlayer.SetSequenceId(string sequenceId) => IsAvailable() ? Wrap(Instance.SetSequenceId(sequenceId)) : Empty.AudioPlayer;
        float IAudioPlayer.GetVolume() => IsAvailable(false) ? ((IAudioPlayer)Instance).GetVolume() : 0f;

        void IAudioStoppable.Stop() => Instance?.Stop();
        void IAudioStoppable.Stop(Action onFinished) => Instance?.Stop(onFinished);
        void IAudioStoppable.Stop(float fadeOut) => Instance?.Stop(fadeOut);
        void IAudioStoppable.Stop(float fadeOut, Action onFinished) => Instance?.Stop(fadeOut, onFinished);
        void IAudioStoppable.Pause() => Instance?.Pause();
        void IAudioStoppable.Pause(float fadeOut) => Instance?.Pause(fadeOut);
        void IAudioStoppable.UnPause() => Instance?.UnPause();
        void IAudioStoppable.UnPause(float fadeOut) => Instance?.UnPause(fadeOut);

        IAudioPlayer IAudioPlayer.OnStart(Action<IAudioPlayer> onStart) => IsAvailable() ? Wrap(Instance.OnStart(onStart)) : Empty.AudioPlayer;
        IAudioPlayer IAudioPlayer.OnUpdate(Action<IAudioPlayer> onUpdate) => IsAvailable() ? Wrap(Instance.OnUpdate(onUpdate)) : Empty.AudioPlayer;
        IAudioPlayer IAudioPlayer.OnPause(Action<IAudioPlayer> onPause) => IsAvailable() ? Wrap(Instance.OnPause(onPause)) : Empty.AudioPlayer;
        IAudioPlayer IAudioPlayer.OnEnd(Action<SoundID> onEnd) => IsAvailable() ? Wrap(Instance.OnEnd(onEnd)) : Empty.AudioPlayer;

        public IAudioPlayer SetFadeInEase(Ease ease) => IsAvailable() ? Wrap(Instance.SetFadeInEase(ease)) : Empty.AudioPlayer;
        public IAudioPlayer SetFadeOutEase(Ease ease) => IsAvailable() ? Wrap(Instance.SetFadeOutEase(ease)) : Empty.AudioPlayer;

        IAudioSourceProxy IAudioPlayer.AudioSource
        {
            get
            {
                if (Instance && Instance is IAudioPlayer player)
                {
                    return player.AudioSource;
                }
                return null;
            }
        }

        SoundID IMusicPlayer.ID => ID;
        IAudioPlayer ISchedulable.SetScheduledStartTime(double dspTime) => IsAvailable() ? Wrap(Instance.SetScheduledStartTime(dspTime)) : Empty.AudioPlayer;
        IAudioPlayer ISchedulable.SetScheduledEndTime(double dspTime) => IsAvailable() ? Wrap(Instance.SetScheduledEndTime(dspTime)) : Empty.AudioPlayer;
        IAudioPlayer ISchedulable.SetDelay(float time) => IsAvailable() ? Wrap(Instance.SetDelay(time)) : Empty.AudioPlayer;

        public void GetOutputData(float[] samples, int channels) => Instance?.GetOutputData(samples, channels);
        public void GetSpectrumData(float[] samples, int channels, FFTWindow window) => Instance?.GetSpectrumData(samples, channels, window);
        IAudioPlayer IAudioPlayer.AddAudioEffect<T, TProxy>(Action<TProxy> onSet)
            => IsAvailable() ? Wrap(((IAudioPlayer)Instance).AddAudioEffect<T, TProxy>(onSet)) : Empty.AudioPlayer;

        IAudioPlayer IAudioPlayer.RemoveAudioEffect<T>()
            => IsAvailable() ? Wrap(((IAudioPlayer)Instance).RemoveAudioEffect<T>()) : Empty.AudioPlayer;

        IAudioPlayer IAudioPlayer.OnAudioFilterRead(Action<float[], int> onAudioFilterRead)
            => IsAvailable() ? Wrap(Instance.OnAudioFilterRead(onAudioFilterRead)) : Empty.AudioPlayer;

        // These decorator methods will only be called when there's corresponding instance in the _decorators list
        IAudioPlayer IMusicPlayer.SetTransition(Transition transition, StopMode stopMode, float overrideFade)
            => Wrap(GetDecorator<MusicPlayer>()?.SetTransition(transition, stopMode, overrideFade));

#if !UNITY_WEBGL
        IPlayerEffect IPlayerEffect.QuietOthers(float othersVol, float fadeTime)
            => Wrap(GetDecorator<DominatorPlayer>()?.QuietOthers(othersVol, fadeTime));
        IPlayerEffect IPlayerEffect.QuietOthers(float othersVol, Fading fading)
            => Wrap(GetDecorator<DominatorPlayer>()?.QuietOthers(othersVol, fading));
        IPlayerEffect IPlayerEffect.LowPassOthers(float freq, float fadeTime)
            => Wrap(GetDecorator<DominatorPlayer>()?.LowPassOthers(freq, fadeTime));
        IPlayerEffect IPlayerEffect.LowPassOthers(float freq, Fading fading)
            => Wrap(GetDecorator<DominatorPlayer>()?.LowPassOthers(freq, fading));

        IPlayerEffect IPlayerEffect.HighPassOthers(float freq, float fadeTime)
            => Wrap(GetDecorator<DominatorPlayer>()?.HighPassOthers(freq, fadeTime));

        IPlayerEffect IPlayerEffect.HighPassOthers(float freq, Fading fading)
            => Wrap(GetDecorator<DominatorPlayer>()?.HighPassOthers(freq, fading));
#endif
#pragma warning restore UNT0008
        #endregion

        public override void UpdateInstance(AudioPlayer newInstance)
        {
            if (!IsAvailable(false))
            {
                base.UpdateInstance(newInstance);
                return;
            }

            if (Instance.TransferOnUpdates(out var onUpdateDelegates))
            {
                foreach (var onUpdate in onUpdateDelegates)
                {
                    newInstance.OnUpdate(onUpdate as Action<IAudioPlayer>);
                }
            }

            if (Instance.TransferOnEnds(out var onEndDelegates))
            {
                foreach (var onEnd in onEndDelegates)
                {
                    newInstance.OnEnd(onEnd as Action<SoundID>);
                }
            }

            if (Instance.TransferOnPauses(out var onPauseDelegates))
            {
                foreach (var onPause in onPauseDelegates)
                {
                    newInstance.OnPause(onPause as Action<IAudioPlayer>);
                }
            }

            if (Instance.TransferDecorators(out var decorators))
            {
                foreach (var decorator in decorators)
                {
                    decorator.UpdateInstance(newInstance);
                }
                newInstance.SetDecorators(decorators);
            }

            Instance.TransferAddedEffectComponents(newInstance);

            base.UpdateInstance(newInstance);
        }

        /// <summary>
        /// Executing a method and returning the instance of this wrapper instead of the method's return value
        /// </summary>
        private IAudioPlayer Wrap(object method)
        {
            return this;
        }

        /// <inheritdoc cref="Wrap"/>
        private IMusicPlayer Wrap(IMusicPlayer musicPlayer)
        {
            return this;
        }

        /// <inheritdoc cref="Wrap"/>
        private IPlayerEffect Wrap(IPlayerEffect dominator)
        {
            return this;
        }

        private T GetDecorator<T>() where T : AudioPlayerDecorator
        {
            if (Instance.TryGetDecorator<T>(out var result))
            {
                return result;
            }
            return null;
        }

        public static explicit operator AudioPlayer(AudioPlayerInstanceWrapper wrapper) => wrapper.IsAvailable(false) ? wrapper.Instance : null;
    }
}
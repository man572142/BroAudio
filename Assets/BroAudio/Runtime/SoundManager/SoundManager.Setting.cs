using System.Collections;
using System.Collections.Generic;
using Ami.BroAudio.Data;
using Ami.BroAudio.Tools;
using Ami.Extension;
using UnityEngine;

namespace Ami.BroAudio.Runtime
{
    public partial class SoundManager : MonoBehaviour
    {
        private RuntimeSetting _setting = null;
        public RuntimeSetting Setting
        {
            get
            {
                _setting ??= Resources.Load<RuntimeSetting>(BroName.RuntimeSettingName);

                if (!_setting)
                {
                    _setting = ScriptableObject.CreateInstance<RuntimeSetting>();
                    Debug.LogWarning(Utility.LogTitle + $"Can't load {BroName.RuntimeSettingName}.asset, all setting values will be as default. " +
                        "If your setting file is missing. Please reimport it from the asset package");
                }
                return _setting;
            }
        }

        public static Ease FadeInEase => Instance.Setting.DefaultFadeInEase;
        public static Ease FadeOutEase => Instance.Setting.DefaultFadeOutEase;
        public static Ease SeamlessFadeIn => Instance.Setting.SeamlessFadeInEase;
        public static Ease SeamlessFadeOut => Instance.Setting.SeamlessFadeOutEase;
    }
}
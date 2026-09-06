ARG BASE_IMAGE
FROM ${BASE_IMAGE}

# The stock unityci/editor image installs no audio libraries at all, so FMOD finds no output
# device and falls back to a non-realtime null sink pumped by the frame loop - measured at
# 747.94x wall time on a GitHub runner. At that ratio a voice starts and finishes between two
# frames, which makes every PlayMode timing test a coin flip.
#
# A PulseAudio null sink is timer-paced at realtime, which is the clock those tests need. We are
# not after audible sound, only a sink that advances at 1x. libasound2-plugins plus the asound.conf
# below routes ALSA to PulseAudio too, since FMOD does not reliably fall back on its own.
RUN apt-get update \
 && apt-get install -y --no-install-recommends pulseaudio libasound2-plugins \
 && rm -rf /var/lib/apt/lists/* \
 && echo 'load-module module-null-sink sink_name=ci' >> /etc/pulse/system.pa \
 && printf 'pcm.!default { type pulse }\nctl.!default { type pulse }\n' > /etc/asound.conf

# /usr/bin/unity-editor sources every readable *.sh in this directory before launching the Editor,
# which is the image's supported pre-launch hook. System mode is what lets the daemon run as root.
RUN printf '%s\n' \
      'pulseaudio --system --disallow-exit --exit-idle-time=-1 --daemonize > /dev/null 2>&1 || true' \
      > /usr/bin/unity-editor.d/00-audio.sh

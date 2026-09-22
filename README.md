# Beep Tone Injector

A Windows tray app that mixes a recording beep into the microphone and sends that mix to a virtual audio cable. The softphone uses the cable as its microphone, so the remote party and the recording hear the beep.

It does not record the call. It does not decide whether a beep is required.

## Tone

Setup saves these in `%LOCALAPPDATA%\BeepTone\config.json`. A new install starts at the defaults below.

- 1400 Hz, limited to 1260-1540
- 200 ms long, limited to 170-250, with a 50 ms fade at each end so it eases in instead of popping
- Repeats 13 seconds after the previous beep starts, limited to every 12-15 seconds
- Level is the fixed dBFS value from setup. Closer to 0 is louder. It does not rise and fall with other audio on the call.

The beep is timed from the audio clock, so it does not drift over a long shift. The first beep plays as soon as the mixer starts.

The mix uses only the local microphone. The other person's voice is not mixed back in, because that would echo them to themselves.

## First run

Agents do not need to be local administrators for a normal shift.

1. Install [Windows PowerShell 5.1](https://learn.microsoft.com/powershell/scripting/install/installing-windows-powershell), which is already on Windows 10 and 11.
2. Start the app:

```powershell
powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File ".\BeepTone.ps1"
```

The console closes and a setup window opens.

3. Click **Install Virtual Cable**. That downloads [VB-Audio Virtual Cable](https://vb-audio.com/Cable/) and runs its installer. Windows asks for an administrator password on a standard user account. If that prompt is cancelled, the cable is not installed. If the cable does not appear after install, sign out or reboot and start the app again.
4. Choose the physical microphone. The cable's own recording device is hidden so the mix cannot loop.
5. Set the tone frequency, length, spacing, fade, and level.
6. Leave **Set CABLE Output as the default communications microphone** checked if the softphone follows the Windows communications device.
7. Save. This also registers the tasks that start the app at sign-in and bring it back if it stops.

In the softphone, set the microphone to **CABLE Output (VB-Audio Virtual Cable)**. Do not also select the physical microphone there.

Headphones avoid the speaker feeding back into the microphone. The mixer adds about 20 ms of delay.

## Softphone audio processing

The beep is a tone inside the microphone signal. Noise removal, noise suppression, and automatic microphone volume treat that tone as noise. They make it louder, quieter, or drop it out as the other audio on the call changes.

In Webex, set Settings, Audio, Smart audio, Microphone audio to **Music mode**. Leave Noise removal, Optimize for my voice, and Optimize for all voices off. Also turn off automatic microphone volume if that option is shown.

Any other softphone needs the same thing. Turn off noise removal, noise suppression, and automatic gain on the microphone that receives the cable.

## Tray

The icon has no Quit. It turns amber for a moment each time a beep is sent. The menu is status, **Beep now**, **Hear beep on this PC**, **Setup**, **Open readme**, and **Open log**.

**Beep now** plays the tone into the call. **Hear beep on this PC** plays the same tone through this computer's default speakers or headset so the level can be checked, and does not send it into the call. **Open readme** opens `README.md` from the same folder as `BeepTone.ps1`.

Muting the microphone in the softphone, in Windows, or on the headset also mutes this beep, because the beep is part of that microphone signal.

## Keeping it running

The mixer restarts itself if the microphone or cable drops. It writes a heartbeat every second. A per-user task starts it at sign-in. Another task checks about once a minute and starts it again if the process is gone or the heartbeat is older than 90 seconds.

An administrator can force it to stay stopped:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\BeepTone.ps1" -Stop
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\BeepTone.ps1" -Start
```

`-Stop` asks Windows to elevate, sets `HKLM\Software\BeepTone\Disabled`, and ends the mixer. The tasks will not bring it back, including after the next sign-in, until an administrator runs `-Start`. A standard user who cancels the approval prompt does not stop the beep. `-Start` clears the flag and starts the mixer as the signed-in user, not as administrator.

## Other commands

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\BeepTone.ps1" -ListDevices
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\BeepTone.ps1" -SelfTest
```

`-SelfTest` checks the beep length, duration, and frequency without using a microphone.

Settings and the log are in `%LOCALAPPDATA%\BeepTone\`. When `beep-tone.log` passes 512 KB it is renamed to `beep-tone-YYYYMMDD-HHMMSS.log` and a new log starts. Only the seven newest archives are kept. The script file itself can live in a read-only folder. `config.json` can name a different playback device, such as a VoiceMeeter input, by changing `renderDeviceName` to a substring of that device's name.

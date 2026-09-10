Recorded voice for the cluster's spoken alerts
==============================================

Drop WAV files in this folder to replace the Windows speech voice with a real
recording. The whole folder is copied next to HolyLogger.exe when the program is
built, and the program picks the files up from there.

The names must be exactly these (the ".wav" matters, the folder name matters):

    New Country.wav     someone saying   "New country"
    Alert Call.wav      someone saying   "Alert call"
    Unconfirmed.wav     someone saying   "Unconfirmed"

Notes
-----
* One file is enough. Any phrase without a recording is still spoken by Windows,
  so the three can be added one at a time.
* Once all three are here, the "Spoken by" voice picker in Cluster Settings
  disappears, because there is no longer a voice for it to choose.
* Plain WAV, please - not MP3 renamed to .wav. Any normal sample rate is fine.
  Keep them short and leave no silence at the start, or the alert sounds late.
* To go back to the Windows voice, delete the files from the installed folder
  (next to HolyLogger.exe) and restart.

Adding them to the installer
----------------------------
The MSI does not pick this folder up on its own. In the setup project
(HolyLogger_x86), add a "Voice" folder under the application folder and add the
WAV files to it, the same way Data\callsigns_merged_big.txt was added.

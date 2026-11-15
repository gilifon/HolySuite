# Google Drive Sync

Google Drive Sync keeps your log the same on all your computers. Each computer sends its changes to a folder called "HolyLogger Sync" in your Google Drive, and picks up the changes made on your other computers.

Changes made on one computer reach the others within a few minutes. HolyLogger can see only the files it makes in that folder, nothing else in your Google Drive.

## Turning it on

1. Open **File > Google Drive Sync**. You can also click the cloud at the top right of the main window, or the **Database not synced** button in the Log Manager.
2. Click **Sign in with Google**.

![The first question: click Sign in with Google](sync-sign-in.png)

3. Your web browser opens on Google's page. Choose your Google account and allow HolyLogger to keep its files in your Google Drive.
4. Back in HolyLogger, a message says **Signed in as** and your email address.

![Signed in: this computer now uses Google Drive Sync](sync-signed-in.png)

## The cloud at the top

The cloud at the top right of the main window, just left of the green share button, shows how sync is doing:

![Where the cloud is: top right, next to the share button](sync-cloud-place.png)

![The three colours of the cloud](sync-cloud.png)

- **Green with a tick:** synced.
- **Orange with "!":** signed in, but Google Drive cannot be reached right now. Your changes wait on this computer and are sent when Google Drive is back.
- **Red with an X:** not synced. The log is only on this computer.

Hold the mouse over the cloud to read more. Click it to open Google Drive Sync.

## Adding another computer

A few minutes after you sign in on your first computer, it puts a whole copy of your log in your Google Drive. It sends a fresh copy once a week.

1. Install HolyLogger on the other computer.
2. Sign in there with the same Google account, as in "Turning it on".
3. HolyLogger asks whether to download your log from Google Drive. Click **Download it**.

![On the new computer: click Download it](sync-download.png)

4. HolyLogger restarts with your log on this computer.

If that computer already has QSOs, HolyLogger asks whether to use the Drive log there too:

![A computer that already has QSOs is asked this instead](sync-use-drive-log.png)

- **Use the Drive log:** your log from Google Drive is put on this computer. The log that was there is kept as a backup.
- **Keep this log:** this computer keeps its own log.

## If you have no Gmail address

You do not need Gmail. You can make a free Google account with the email address you already have, for example a Yahoo or Outlook address, or the one from your internet provider.

1. Open https://accounts.google.com/signin in your web browser.
2. Click **Create account**, then choose **For my personal use**.
3. Fill in your name and the other details Google asks for.
4. Click **Use your existing email**.
5. Type your email address and click **Next**.
6. Google sends a code to that email address. Type the code and click **Next**.
7. Choose a password and finish the steps on the screen.

Now sign in to Google Drive Sync with this account, as in "Turning it on".

Google Drive Sync works only with Google Drive. Dropbox, OneDrive and other services cannot be used for it.

## Turning it off

Open **File > Google Drive Sync** and click **Sign out**. This computer stops sending its changes to Google Drive. Your log stays on this computer.

# CW Decoder

The CW Decoder listens to the sound your radio sends the computer over the USB cable, and writes what it hears. For it to work, three things must be right: the radio must send the sound, Windows must record it loudly enough, and HolyLogger must listen to the right device.

## Checking the sound in HolyLogger

Open **Options > General** and look at the **Input Device** box.

- **Listen to:** choose the radio's sound device. It is usually called "USB Audio CODEC".
- The bar and the word beside it show the sound arriving now: **Good**, **Too weak**, **Too loud** or **No sound**.
- **Windows recording level** opens Windows' recording devices. Choose the radio's device, then **Properties**, then **Levels**, and raise or lower the level until the word says **Good**.
- **Set the radio for CW decoding** sets the radio's own sound settings for you (IC-7610, IC-7300 and IC-7300MK2). A window shows what will be changed. Nothing is sent before you press **OK**, and afterwards it shows whether the radio took each setting.

To see your own sending in the decoder, the radio must also send its side tone to the computer. HolyLogger turns this on by itself on the IC-7610, IC-7300 and IC-7300MK2 when the CW Decoder opens.

## Radio settings for the sound

These are the radio's own menu items for the sound it sends the computer, with the names your radio's manual uses.

### Icom IC-7610

**MENU > SET > Connectors > USB AF/IF Output**

- **Output Select:** AF. On IF the radio sends a signal the decoder cannot read.
- **AF Output Level:** the level of the sound to the computer (50% from the factory).
- **AF SQL:** OFF (Open). When ON, the sound stops whenever the squelch is closed.
- **AF Beep/Speech... Output:** ON, to see your own sending. It adds the radio's beeps and voice to the sound too.

### Icom IC-7300

**MENU > SET > Connectors**

- **ACC/USB Output Select:** AF.
- **ACC/USB AF Output Level:** the level of the sound to the computer (50% from the factory).
- **ACC/USB AF SQL:** OFF (OPEN).
- **ACC/USB AF Beep/Speech... Output:** ON. The manual says it adds beeps and speech; it does not say whether it adds the side tone.

### Icom IC-7300MK2

**MENU > SET > Connectors > USB AF/IF Output**

- **Output Select:** AF.
- **AF Output Level:** the level of the sound to the computer.
- **AF SQL:** OFF (Open).
- **AF Beep/Speech... Output:** ON.

### Kenwood TS-590SG

- **Menu 72, Audio level of USB output for data communications:** the level of the sound to the computer (0 to 9, 4 from the factory).
- **Menu 75, Mixing beep tones for ACC2/USB audio output:** ON, to see your own sending. It adds the beeps, the side tone and the voice guide to the sound.

### Kenwood TS-590S

- **Menu 65, USB audio output level:** the level of the sound to the computer.
- **Menu 68, External AF output beep mix:** ON, to add the beeps to the sound.

### Kenwood TS-890S

- **Menu [7-08], USB: Audio Output Level:** the level of the sound to the computer (100 from the factory).
- **Menu [7-11], Audio Output Type (Rear Connectors):** All (the factory setting). All adds the beeps, the voice guidance and the side tone to the sound, so you can see your own sending.

### Yaesu FTdx10, FTdx101, FT-991A

- **CW OUT LEVEL:** the manual says it sets the level of the CW signal from the RTTY/DATA jack (50 from the factory). On the FT-991A it is menu 054.
- **CW OUT SELECT** (FTdx101 only): which receiver, MAIN or SUB, is sent out in CW (MAIN from the factory).

The Yaesu manuals have no setting that sends the side tone to the computer.

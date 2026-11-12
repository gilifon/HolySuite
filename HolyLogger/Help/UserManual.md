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

The cloud at the top right of the main window shows how sync is doing:

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

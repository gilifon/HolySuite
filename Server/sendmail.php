<?php

// ============================================================================
// HolyLogger support mail
//
// Receives Help > Support > "Send mail to the developers" and posts it on as
// one email. Replaces the first version, which took a single .txt attachment
// and nothing else.
//
// WHAT CHANGED, AND WHY
//   * SCREENSHOTS. The operator can paste pictures into the message with
//     Ctrl+V. They arrive here as images[] and are shown INSIDE the mail, in
//     the order they were pasted, as well as being attachments - a picture the
//     reader has to open separately is a picture the reader does not look at.
//   * REPLY-TO. The mail must be sent FROM this domain or it is spam by every
//     modern rule, so From stays holylogger@iarc.org. The operator's own
//     address goes in Reply-To, so pressing Reply answers the person who wrote
//     rather than this mailbox.
//   * The final MIME boundary. The old file ended with '--\r\n' in SINGLE
//     quotes, which puts the literal characters backslash-r-backslash-n into
//     the message instead of a line break, leaving every mail malformed.
//   * Header injection. Anything that reaches a mail header - the subject and
//     the reply address - has its line breaks stripped first. Without that, a
//     newline typed into the subject box can add headers of its own.
//
// THE LIMITS ARE THE THINGS TO EDIT. They are all together at the top. Note
// that PHP's own upload_max_filesize and post_max_size must be at least as
// large as MAX_TOTAL_BYTES or the request never reaches this script at all -
// the browser/client is simply cut off, and $_POST arrives empty.
// ============================================================================

header('Content-Type: application/json; charset=utf-8');

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

const FROM_EMAIL      = 'holylogger@iarc.org';   // must be a mailbox on this domain (SPF/DKIM)
const RECIPIENT_EMAIL = 'holylogger@iarc.org';
const SUBJECT_PREFIX  = 'HolyLogger Support';    // the operator's own title is appended

const MAX_CALLSIGN_LENGTH = 50;
const MAX_NAME_LENGTH     = 200;
const MAX_EMAIL_LENGTH    = 254;
const MAX_TITLE_LENGTH    = 200;
const MAX_MESSAGE_LENGTH  = 10000;

const MAX_LOG_BYTES   = 5  * 1024 * 1024;   // the error log (.txt), required
const MAX_IMAGE_BYTES = 5  * 1024 * 1024;   // each pasted picture
const MAX_IMAGES      = 6;                  // how many pictures one message may carry
const MAX_TOTAL_BYTES = 20 * 1024 * 1024;   // everything together

const ALLOWED_IMAGE_MIME = [
    'image/png'  => 'png',
    'image/jpeg' => 'jpg',
    'image/gif'  => 'gif',
    'image/bmp'  => 'bmp',
];

// ---------------------------------------------------------------------------
// Answering
// ---------------------------------------------------------------------------

function respond($status_code, $success, $error = null)
{
    http_response_code($status_code);

    $response = ['success' => $success];
    if ($error !== null) {
        $response['error'] = $error;
    }

    echo json_encode($response);
    exit;
}

// A value that is going into a mail HEADER. Everything from the first control
// character on is dropped: one newline in here and the sender chooses its own
// headers, recipients included.
function header_safe($value)
{
    $value = preg_replace('/[\r\n\t]+/', ' ', (string)$value);
    return trim(preg_replace('/[[:cntrl:]]/', '', $value));
}

function html_safe($value)
{
    return htmlspecialchars((string)$value, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
}

// ---------------------------------------------------------------------------
// Only POST
// ---------------------------------------------------------------------------

if ($_SERVER['REQUEST_METHOD'] !== 'POST') {
    respond(405, false, 'Method Not Allowed');
}

// An empty $_POST on a POST request nearly always means the upload was larger
// than PHP itself accepts, in which case nothing here ever ran on real data.
// Saying so beats "callsign is required", which sends the operator hunting for
// a field they filled in.
if (empty($_POST) && empty($_FILES)) {
    respond(413, false, 'The message was too large for the server to accept.');
}

// ---------------------------------------------------------------------------
// The text fields
// ---------------------------------------------------------------------------

$callsign        = trim($_POST['callsign'] ?? '');
$name            = trim($_POST['name'] ?? '');
$email           = trim($_POST['email'] ?? '');
$support_title   = trim($_POST['title'] ?? '');
$support_message = trim($_POST['message'] ?? '');

if ($callsign === '' || $name === '' || $email === '' ||
    $support_title === '' || $support_message === '') {
    respond(400, false, 'Callsign, name, email, title and message are required.');
}

if (mb_strlen($callsign, 'UTF-8') > MAX_CALLSIGN_LENGTH) {
    respond(400, false, 'Callsign is too long.');
}
if (mb_strlen($name, 'UTF-8') > MAX_NAME_LENGTH) {
    respond(400, false, 'Name is too long.');
}
if (mb_strlen($email, 'UTF-8') > MAX_EMAIL_LENGTH) {
    respond(400, false, 'Email address is too long.');
}
if (mb_strlen($support_title, 'UTF-8') > MAX_TITLE_LENGTH) {
    respond(400, false, 'Title is too long.');
}
if (mb_strlen($support_message, 'UTF-8') > MAX_MESSAGE_LENGTH) {
    respond(400, false, 'Message is too long.');
}

if (!filter_var($email, FILTER_VALIDATE_EMAIL)) {
    respond(400, false, 'Invalid email address.');
}

// ---------------------------------------------------------------------------
// The error log - required, and text
// ---------------------------------------------------------------------------

if (!isset($_FILES['attachment'])) {
    respond(400, false, 'Attachment is required.');
}

$log = $_FILES['attachment'];

if (!is_array($log) || ($log['error'] ?? UPLOAD_ERR_NO_FILE) !== UPLOAD_ERR_OK) {
    respond(400, false, 'File upload failed.');
}
if ($log['size'] > MAX_LOG_BYTES) {
    respond(400, false, 'The log file is too large. Maximum size is '
        . (int)(MAX_LOG_BYTES / 1024 / 1024) . ' MB.');
}
if ($log['size'] === 0) {
    respond(400, false, 'The uploaded log file is empty.');
}
if (strtolower(pathinfo($log['name'], PATHINFO_EXTENSION)) !== 'txt') {
    respond(400, false, 'The log must be a .txt file.');
}

$finfo = new finfo(FILEINFO_MIME_TYPE);
$log_mime = $finfo->file($log['tmp_name']);
if (!in_array($log_mime, ['text/plain', 'text/csv', 'application/octet-stream'], true)) {
    respond(400, false, 'The log is not a text file.');
}

$log_content = @file_get_contents($log['tmp_name']);
if ($log_content === false) {
    respond(500, false, 'Unable to read the uploaded log.');
}

$total_bytes = strlen($log_content);

// ---------------------------------------------------------------------------
// The pasted pictures - optional, images[] , any number up to MAX_IMAGES
// ---------------------------------------------------------------------------

$images = [];

if (isset($_FILES['images']) && is_array($_FILES['images']['name'])) {
    $count = count($_FILES['images']['name']);

    if ($count > MAX_IMAGES) {
        respond(400, false, 'Too many pictures. At most ' . MAX_IMAGES . ' may be sent.');
    }

    for ($i = 0; $i < $count; $i++) {
        $error = $_FILES['images']['error'][$i];

        if ($error === UPLOAD_ERR_NO_FILE) {
            continue;                       // an empty slot is not a fault
        }
        if ($error !== UPLOAD_ERR_OK) {
            respond(400, false, 'A picture failed to upload.');
        }

        $size = (int)$_FILES['images']['size'][$i];
        if ($size === 0) {
            continue;
        }
        if ($size > MAX_IMAGE_BYTES) {
            respond(400, false, 'A picture is too large. Maximum size is '
                . (int)(MAX_IMAGE_BYTES / 1024 / 1024) . ' MB each.');
        }

        $tmp = $_FILES['images']['tmp_name'][$i];

        // IS IT REALLY A PICTURE? Not the name, not the declared type - the
        // bytes. getimagesize reads the actual header and fails on anything
        // that only claims to be an image.
        $mime = $finfo->file($tmp);
        if (!isset(ALLOWED_IMAGE_MIME[$mime]) || @getimagesize($tmp) === false) {
            respond(400, false, 'Only PNG, JPEG, GIF and BMP pictures can be sent.');
        }

        $content = @file_get_contents($tmp);
        if ($content === false) {
            respond(500, false, 'Unable to read an uploaded picture.');
        }

        $total_bytes += strlen($content);
        if ($total_bytes > MAX_TOTAL_BYTES) {
            respond(400, false, 'The message is too large. Everything together must be under '
                . (int)(MAX_TOTAL_BYTES / 1024 / 1024) . ' MB.');
        }

        $images[] = [
            'content' => $content,
            'mime'    => $mime,
            // Named by us, never by the sender: a filename from outside is a
            // path waiting to be somewhere it should not be.
            'name'    => 'screenshot-' . (count($images) + 1) . '.' . ALLOWED_IMAGE_MIME[$mime],
            'cid'     => 'holylogger-img-' . (count($images) + 1) . '@iarc.org',
        ];
    }
}

// ---------------------------------------------------------------------------
// The message body
// ---------------------------------------------------------------------------

$safe_title    = html_safe($support_title);
$safe_message  = nl2br(html_safe($support_message));
$safe_name     = html_safe($name);
$safe_callsign = html_safe($callsign);
$safe_email    = html_safe($email);

$html  = '<div style="font-family:Segoe UI,Arial,sans-serif;font-size:14px">';
$html .= '<p><strong>' . $safe_callsign . '</strong> &nbsp; ' . $safe_name . '<br>';
$html .= '<a href="mailto:' . $safe_email . '">' . $safe_email . '</a></p>';
$html .= '<hr>';
$html .= '<p><strong>' . $safe_title . '</strong></p>';
$html .= '<p>' . $safe_message . '</p>';

// The pictures, in the body, in the order they were pasted.
foreach ($images as $index => $image) {
    $html .= '<p style="margin:18px 0 4px 0"><em>Picture ' . ($index + 1) . '</em></p>';
    $html .= '<img src="cid:' . $image['cid'] . '" '
           . 'style="max-width:900px;border:1px solid #ccc" alt="Picture ' . ($index + 1) . '">';
}

$html .= '</div>';

// A plain-text part as well. A mail with no text alternative is one more thing
// for a spam filter to hold against it, and some readers show nothing else.
$plain  = $callsign . '  ' . $name . "\r\n" . $email . "\r\n\r\n";
$plain .= $support_title . "\r\n\r\n" . $support_message . "\r\n\r\n";
$plain .= count($images) . ' picture(s) attached, plus the error log.' . "\r\n";

// ---------------------------------------------------------------------------
// Build the MIME message
//
//   multipart/mixed                 the log, as a downloadable attachment
//     multipart/related             the message and the pictures it shows
//       multipart/alternative       plain text and HTML
//       image parts (Content-ID)
//     text/plain attachment (log)
// ---------------------------------------------------------------------------

$mixed_boundary   = 'mix_'  . md5(uniqid((string)mt_rand(), true));
$related_boundary = 'rel_'  . md5(uniqid((string)mt_rand(), true));
$alt_boundary     = 'alt_'  . md5(uniqid((string)mt_rand(), true));

$subject = header_safe(SUBJECT_PREFIX . ': ' . $support_title);

// Non-ASCII in a subject has to be encoded or it arrives as rubbish.
$subject = '=?UTF-8?B?' . base64_encode($subject) . '?=';

$headers  = 'From: HolyLogger <' . FROM_EMAIL . '>' . "\r\n";
$headers .= 'Reply-To: ' . header_safe($name) . ' <' . header_safe($email) . '>' . "\r\n";
$headers .= 'MIME-Version: 1.0' . "\r\n";
$headers .= 'X-Mailer: HolyLogger' . "\r\n";
$headers .= 'Content-Type: multipart/mixed; boundary="' . $mixed_boundary . '"' . "\r\n";

$body = '';

// -- the message itself, with its pictures ----------------------------------
$body .= '--' . $mixed_boundary . "\r\n";
$body .= 'Content-Type: multipart/related; boundary="' . $related_boundary . '"' . "\r\n";
$body .= "\r\n";

$body .= '--' . $related_boundary . "\r\n";
$body .= 'Content-Type: multipart/alternative; boundary="' . $alt_boundary . '"' . "\r\n";
$body .= "\r\n";

$body .= '--' . $alt_boundary . "\r\n";
$body .= 'Content-Type: text/plain; charset=UTF-8' . "\r\n";
$body .= 'Content-Transfer-Encoding: base64' . "\r\n";
$body .= "\r\n";
$body .= chunk_split(base64_encode($plain));
$body .= "\r\n";

$body .= '--' . $alt_boundary . "\r\n";
$body .= 'Content-Type: text/html; charset=UTF-8' . "\r\n";
$body .= 'Content-Transfer-Encoding: base64' . "\r\n";
$body .= "\r\n";
$body .= chunk_split(base64_encode($html));
$body .= "\r\n";

$body .= '--' . $alt_boundary . '--' . "\r\n";
$body .= "\r\n";

foreach ($images as $image) {
    $body .= '--' . $related_boundary . "\r\n";
    $body .= 'Content-Type: ' . $image['mime'] . '; name="' . $image['name'] . '"' . "\r\n";
    $body .= 'Content-Transfer-Encoding: base64' . "\r\n";
    $body .= 'Content-ID: <' . $image['cid'] . '>' . "\r\n";
    $body .= 'Content-Disposition: inline; filename="' . $image['name'] . '"' . "\r\n";
    $body .= "\r\n";
    $body .= chunk_split(base64_encode($image['content']));
    $body .= "\r\n";
}

$body .= '--' . $related_boundary . '--' . "\r\n";
$body .= "\r\n";

// -- the error log ----------------------------------------------------------
$log_name = 'holylogger-log.txt';

$body .= '--' . $mixed_boundary . "\r\n";
$body .= 'Content-Type: text/plain; charset=UTF-8; name="' . $log_name . '"' . "\r\n";
$body .= 'Content-Disposition: attachment; filename="' . $log_name . '"' . "\r\n";
$body .= 'Content-Transfer-Encoding: base64' . "\r\n";
$body .= "\r\n";
$body .= chunk_split(base64_encode($log_content));
$body .= "\r\n";

// DOUBLE QUOTES. The old file wrote this line in single quotes, so every
// message ended with the literal text backslash-r-backslash-n instead of a
// line break and the last boundary was malformed.
$body .= '--' . $mixed_boundary . '--' . "\r\n";

// ---------------------------------------------------------------------------
// Send
// ---------------------------------------------------------------------------

// -f sets the envelope sender, which is what SPF is actually checked against.
if (!mail(RECIPIENT_EMAIL, $subject, $body, $headers, '-f' . FROM_EMAIL)) {
    respond(500, false, 'Unable to send support request.');
}

respond(200, true);

<?
include ("db_holylanddb.inc");
ini_set('display_errors',1);
error_reporting(E_ALL);
header('Content-type: application/json');


if (isset ( $_GET ['callsign'] )) {
	$callsign = strtoupper(trim($_GET['callsign']));
} else {
	$callsign = '';
}

if (isset ( $_GET ['operator'] )) {
	$operator = strtoupper(trim($_GET['operator']));
} else {
	$operator = '';
}

if (isset ( $_GET ['frequency'] )) {
	$frequency = $_GET['frequency'];
} else {
	$frequency = false;
}
if (isset ( $_GET ['mode'] )) {
	$mode = $_GET['mode'];
} else {
	$mode = false;
}
if (isset ( $_GET ['machine'] )) {
	$machine = $_GET['machine'];
	$query = "INSERT INTO `iarcorg_holylanddb`.`heartbeat` (`callsign`, `operator`, `frequency`, `mode`, `machine`) VALUES ('".$callsign."','".$operator."','".$frequency."','".$mode."','".$machine."') ON DUPLICATE KEY UPDATE `frequency`='".$frequency."',`mode`='".$mode."',`timestamp`=CURRENT_TIMESTAMP";
} else {
	$machine = null;
	$query = "INSERT INTO `iarcorg_holylanddb`.`heartbeat` (`callsign`, `operator`, `frequency`, `mode`) VALUES ('".$callsign."','".$operator."','".$frequency."','".$mode."') ON DUPLICATE KEY UPDATE `frequency`='".$frequency."',`mode`='".$mode."',`timestamp`=CURRENT_TIMESTAMP";
}
$result = mysqli_query($Link,$query) or die('Error: ' . mysqli_error());




?>
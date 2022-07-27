<?
include ("db_holylanddb.inc");
ini_set('display_errors',1);
error_reporting(E_ALL);
header('Content-type: application/json');

//`my_callsign`, `operator`, `my_square`, `my_locator`, `dx_locator`, `frequency`, `band`, `dx_callsign`, `rst_rcvd`, `rst_sent`, `timestamp`, `mode`, `exchange`, `comment`, `name`, `country`, `continent`, `prop_mode`, `sat_name` 

$insertlog = $_POST['insertlog']; 
$qsos = json_decode($insertlog, true);

//$myfile = fopen("test.txt", "w") or die("Unable to open file!");

$query = "INSERT INTO `log` (`my_callsign`, `operator`, `my_square`, `my_locator`, `dx_locator`, `frequency`, `band`, `dx_callsign`, `rst_rcvd`, `rst_sent`, `timestamp`, `mode`, `exchange`, `comment`, `name`, `country`, `continent`, `prop_mode`, `sat_name` ) VALUES ";
 foreach ($qsos as $qso){
	 
	//fwrite($myfile, $qso["id"]); 
	$my_callsign = $qso["my_callsign"];
	$operator = $qso["operator"];
	$my_square = $qso["my_square"];
	$my_locator = $qso["my_locator"];
	$dx_locator = $qso["dx_locator"];
	$frequency = $qso["frequency"];
	$band = $qso["band"];
	$dx_callsign = $qso["dx_callsign"];
	$rst_rcvd = $qso["rst_rcvd"];
	$rst_sent = $qso["rst_sent"];
	$timestamp = $qso["date"].' '.$qso["time"];
	$mode = $qso["mode"];
	$exchange = $qso["exchange"];
	$comment = $qso["comment"];
	$name = $qso["name"];
	$country = $qso["country"];
	$continent = $qso["continent"];
	$prop_mode = $qso["prop_mode"];
	$sat_name = $qso["sat_name" ];
	$query.="('". $my_callsign ."','". $operator ."','". $my_square ."','". $my_locator ."','". $dx_locator ."','". $frequency ."','". $band ."','". $dx_callsign ."','". $rst_rcvd ."','". $rst_sent ."','". $timestamp ."','". $mode ."','". $exchange ."','". $comment ."','". $name ."','". $country ."','". $continent ."','". $prop_mode ."','". $sat_name ."'),";
 }
 $query = rtrim($query, ",");
 //fwrite($myfile, $query);
 //fclose($myfile);

$result = mysqli_query($Link,$query) or die('Error: ' . mysqli_error($Link));
echo json_encode('Log added!');
?>
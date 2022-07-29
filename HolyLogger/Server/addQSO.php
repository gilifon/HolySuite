<?
include ("db_holylanddb.inc");
ini_set('display_errors',1);
error_reporting(E_ALL);
header('Content-type: application/json');

//`my_callsign`, `operator`, `my_square`, `my_locator`, `dx_locator`, `frequency`, `band`, `dx_callsign`, `rst_rcvd`, `rst_sent`, `timestamp`, `mode`, `exchange`, `comment`, `name`, `country`, `continent`, `prop_mode`, `sat_name` 

$data = $_POST['data']; 
$qsos = json_decode($data, true);

//$myfile = fopen("test.txt", "w") or die("Unable to open file!");

$query = "INSERT INTO `log` (`my_callsign`, `operator`, `my_square`, `my_locator`, `dx_locator`, `frequency`, `band`, `dx_callsign`, `rst_rcvd`, `rst_sent`, `timestamp`, `mode`, `exchange`, `comment`, `name`, `country`, `continent`, `prop_mode`, `sat_name`, `soapbox` ) VALUES ";
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
	if (array_key_exists("soapbox",$qso)){$soapbox = $qso["soapbox"];}else{$soapbox = "";}
	$query .= "('". $my_callsign ."','". $operator ."','". $my_square ."','". $my_locator ."','". $dx_locator ."','". $frequency ."','". $band ."','". $dx_callsign ."','". $rst_rcvd ."','". $rst_sent ."','". $timestamp ."','". $mode ."','". $exchange ."','". $comment ."','". $name ."','". $country ."','". $continent ."','". $prop_mode ."','". $sat_name ."','". $soapbox ."'),";
 }
 $query = rtrim($query, ",");
 $query .= " ON DUPLICATE KEY UPDATE `operator` = VALUES(`operator`), `my_locator` = VALUES(`my_locator`), `dx_locator` = VALUES(`dx_locator`), `band` = VALUES(`band`), `rst_rcvd` = VALUES(`rst_rcvd`), `rst_sent` = VALUES(`rst_sent`), `timestamp` = VALUES(`timestamp`), `comment` = VALUES(`comment`), `name` = VALUES(`name`), `country` = VALUES(`country`), `continent` = VALUES(`continent`), `prop_mode` = VALUES(`prop_mode`), `sat_name` = VALUES(`sat_name`)";
 //fwrite($myfile, $query);
 //fclose($myfile);

$result = mysqli_query($Link,$query) or die('Error: ' . mysqli_error($Link));
echo json_encode('Done!');
?>
<?
include ("db_holylanddb.inc");
ini_set('display_errors',1);
error_reporting(E_ALL);
header('Content-type: application/json');

//`callsign`,`category_op`,`category_mode`,`category_power`,`email`,`name`,`country`,`year`,`qsos`,`points`

$data = $_POST['data']; 
$participant = json_decode($data, true);
$query = "INSERT INTO `participants` (`callsign`,`category_op`,`category_mode`,`category_power`,`email`,`name`,`country`,`year`,`qsos`,`points`) VALUES ";

$callsign = $participant["callsign"];
$category_op = $participant["category_op"];
$category_mode = $participant["category_mode"];
$category_power = $participant["category_power"];
$email = $participant["email"];
$name = $participant["name"];
$country = $participant["country"];
$year = $participant["year"];
$qsos = $participant["qsos"];
$points = $participant["points"];

$query.="('". $callsign ."','". $category_op ."','". $category_mode ."','". $category_power ."','". $email ."','". $name ."','". $country ."','". $year ."','". $qsos ."','". $points ."') ";
$query.="ON DUPLICATE KEY UPDATE `category_op`= '". $category_op. "',`category_mode`= '". $category_mode. "',`category_power`= '". $category_power. "',`email`= '". $email. "',`name`= '". $name. "',`year`= '". $year. "',`qsos`= '". $qsos. "',`points`= '". $points. "'";

$result = mysqli_query($Link,$query) or die('Error: ' . mysqli_error($Link));
echo json_encode('Participant added!');
?>
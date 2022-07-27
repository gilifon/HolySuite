<?
include ("db_holylanddb.inc");
ini_set('display_errors',1);
error_reporting(E_ALL);
header('Content-type: application/json');

// get the POST variable
$insertlog = $_POST['insertlog'];

// extract all the properties of the request
$result = mysqli_query($Link,$insertlog) or die('Error: ' . mysqli_error($Link));
echo json_encode('Log added!');
?>
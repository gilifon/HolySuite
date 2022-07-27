<?
include ("db_holylanddb.inc");
ini_set('display_errors',1);
error_reporting(E_ALL);

$res = [];

$result = mysqli_query($Link,"SELECT * FROM `event` ORDER BY `id` DESC");
while($obj = mysqli_fetch_object($result)) {
$res[] = $obj;
}

header('Content-type: application/json');
echo json_encode($res);
?>
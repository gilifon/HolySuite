<?
include ("db_holylanddb.inc");
ini_set('display_errors',1);
error_reporting(E_ALL);
header('Content-type: application/json');

//$log = mysqli_query($Link,"select * from `log` WHERE my_callsign='4X0RMN'") or die('Error: ' . mysqli_error());
$log = mysqli_query($Link,"select * from `holyland_log`") or die('Error: ' . mysqli_error());

//chanukah
//$participants = mysqli_query($Link,"SELECT `id`, `callsign`, `category_op`, `category_mode`, `category_power`, `email`, `name`, `country`, `year`, `qsos`, `points`, `timestamp`,`is_manual` FROM `participants` WHERE callsign IN ('4X0NER','4X1C','4X2H','4Z3A','4X4N','4X5U','4X6K','4Z7A','4X8H')") or die('Error: ' . mysqli_error());

//craters
//$participants = mysqli_query($Link,"SELECT `id`, `callsign`, `category_op`, `category_mode`, `category_power`, `email`, `name`, `country`, `year`, `qsos`, `points`, `timestamp`,`is_manual` FROM `participants` WHERE callsign IN ('4X0ARF','4X0RMN','4X0KTN','4X0GDL')") or die('Error: ' . mysqli_error());

//maccabiah
$participants = mysqli_query($Link,"SELECT `id`, `callsign`, `category_op`, `category_mode`, `category_power`, `email`, `name`, `country`, `year`, `qsos`, `points`, `timestamp`,`is_manual` FROM `participants` WHERE callsign IN ('4X21MG','4Z21MG')") or die('Error: ' . mysqli_error());

//holyland
//$participants = mysqli_query($Link,"SELECT `id`, `callsign`, `category_op`, `category_mode`, `category_power`, `email`, `name`, `country`, `year`, `qsos`, `points`, `timestamp`,`is_manual` FROM `participants`") or die('Error: ' . mysqli_error());


while($obj = mysqli_fetch_object($log)) {
$res["log"][] = $obj;
}

while($obj = mysqli_fetch_object($participants)) {
$res["participants"][] = $obj;
}

echo json_encode($res);
?>


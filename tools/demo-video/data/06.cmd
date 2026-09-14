curl -s localhost:5199/api/work-orders/WO-20260914-01/delay-risk | jq 'del(.modelDescription, .note, .workOrderNo, .itemCode, .dueDate)'

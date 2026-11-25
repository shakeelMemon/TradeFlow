# Creates SNS topic -> SQS queue (+ DLQ, 5 attempts) for TradeFlow. Requires: aws configure.
param([string]$Region = "ca-central-1")

$topicArn = aws sns create-topic --name tradeflow-trade-orders --region $Region --query TopicArn --output text
$dlqUrl = aws sqs create-queue --queue-name tradeflow-simcorp-dlq --region $Region --query QueueUrl --output text
$dlqArn = aws sqs get-queue-attributes --queue-url $dlqUrl --attribute-names QueueArn --region $Region --query Attributes.QueueArn --output text
$queueUrl = aws sqs create-queue --queue-name tradeflow-simcorp --region $Region --query QueueUrl --output text
$queueArn = aws sqs get-queue-attributes --queue-url $queueUrl --attribute-names QueueArn --region $Region --query Attributes.QueueArn --output text

$policy = @{
    Version = "2012-10-17"
    Statement = @(@{
        Effect = "Allow"
        Principal = @{ Service = "sns.amazonaws.com" }
        Action = "sqs:SendMessage"
        Resource = $queueArn
        Condition = @{ ArnEquals = @{ "aws:SourceArn" = $topicArn } }
    })
} | ConvertTo-Json -Depth 10 -Compress
$redrive = @{ deadLetterTargetArn = $dlqArn; maxReceiveCount = "5" } | ConvertTo-Json -Compress

$attrsFile = Join-Path $env:TEMP "tradeflow-sqs-attrs.json"
@{ Policy = $policy; RedrivePolicy = $redrive; VisibilityTimeout = "30" } | ConvertTo-Json -Compress | Set-Content $attrsFile -Encoding ascii
aws sqs set-queue-attributes --queue-url $queueUrl --attributes "file://$attrsFile" --region $Region

aws sns subscribe --topic-arn $topicArn --protocol sqs --notification-endpoint $queueArn `
    --attributes RawMessageDelivery=true --region $Region | Out-Null

Write-Host "Put these in tradeflow.shared.json under Messaging:Aws and set Provider to 'Aws':"
Write-Host "  TopicArn: $topicArn"
Write-Host "  QueueUrl: $queueUrl"

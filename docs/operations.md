# Operations

Document id: `operations`
Status: draft
Last updated: 2026-10-29

## Purpose

Commands for running, inspecting, and resetting the stack. Replay, backfill,
and schema migration runbooks are added here as they are built.

## Start, Stop, Reset

```sh
docker compose up -d --wait        # start everything and wait for health checks
docker compose stop connect        # stop one service (state is kept)
docker compose start connect
docker compose down                # remove all containers; all data is lost
```

The one-shot jobs (`db-init`, `kafka-init`, `connect-init`) are idempotent and
can be rerun with `docker compose up <job>`.

## Inspect

```sh
docker compose ps
docker compose logs -f connect
curl -s http://localhost:8083/connectors?expand=status
docker compose exec kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server kafka:9092 --list
docker compose exec kafka /opt/kafka/bin/kafka-console-consumer.sh \
  --bootstrap-server kafka:9092 --topic cdc.HrCore.dbo.Salary --from-beginning --property print.key=true
```

## Topics

`kafka-init` creates `hr.employee-pay.v1` (3 partitions) and `hr.dlq`
(1 partition), both with unlimited retention so the read model can always be
rebuilt. Debezium creates the `cdc.*` topics. Broker auto-creation is off.

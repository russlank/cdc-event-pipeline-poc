# CDC Pay Events PoC

Proof of concept: SQL Server change data capture → Debezium → Kafka → versioned,
effective-dated business events (`EmployeeHired`, `SalaryChanged`,
`SalaryCorrected`, `PositionChanged`, `EmployeeTerminated`) with
schema-registry compatibility rules, a bitemporal read model with as-of
queries, replay, idempotency, and monitoring. Synthetic HR/pay data only.
**Not production.**

## Components

| Component | Technology |
|---|---|
| Source database `HrCore` | SQL Server 2022 with CDC and SQL Server Agent |
| Broker | Apache Kafka, single node, KRaft |
| CDC | Kafka Connect + Debezium SQL Server connector |
| Contracts | Avro schemas in Confluent Schema Registry |
| Event Translator | .NET 10 worker: raw CDC → business events |
| Pay History Projector | .NET 10 worker: business events → bitemporal read model in PostgreSQL |
| Pay Query API | ASP.NET Core minimal API: as-of salary queries |
| Monitoring | Prometheus, Grafana, Alertmanager |

## License

MIT, see [LICENSE](LICENSE).

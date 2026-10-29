# Debezium Connect with the Prometheus JMX exporter agent: connector and task status, Debezium
# streaming metrics, and JVM metrics on port 9404 (/metrics).
FROM quay.io/debezium/connect:3.6.2.Final

ADD --chown=kafka:kafka \
    --checksum=sha256:a95983fd96e865d2bcdf911cc500e7c82808c27ab9fd226bf96732b6c3d8c46e \
    https://github.com/prometheus/jmx_exporter/releases/download/1.6.0/jmx_prometheus_javaagent-1.6.0.jar \
    /kafka/jmx/jmx_prometheus_javaagent.jar
COPY --chown=kafka:kafka jmx-exporter.yml /kafka/jmx/jmx-exporter.yml

ENV KAFKA_OPTS="-javaagent:/kafka/jmx/jmx_prometheus_javaagent.jar=9404:/kafka/jmx/jmx-exporter.yml"

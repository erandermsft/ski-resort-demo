package main

import (
	"context"
	"errors"
	"log/slog"
	"os"

	"go.opentelemetry.io/contrib/bridges/otelslog"
	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploggrpc"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc"
	"go.opentelemetry.io/otel/log/global"
	"go.opentelemetry.io/otel/propagation"
	"go.opentelemetry.io/otel/sdk/log"
	"go.opentelemetry.io/otel/sdk/resource"
	"go.opentelemetry.io/otel/sdk/trace"
	semconv "go.opentelemetry.io/otel/semconv/v1.37.0"
)

const serviceName = "datagenerator"

func setupTelemetry(ctx context.Context) (func(context.Context) error, *slog.Logger, error) {
	if !otelExporterConfigured() {
		return func(context.Context) error { return nil }, slog.New(slog.NewTextHandler(os.Stdout, nil)), nil
	}

	res, err := resource.New(ctx,
		resource.WithTelemetrySDK(),
		resource.WithFromEnv(),
		resource.WithHost(),
		resource.WithOS(),
		resource.WithProcess(),
		resource.WithAttributes(semconv.ServiceName(serviceName)),
	)
	if err != nil {
		return nil, nil, err
	}

	traceExporter, err := otlptracegrpc.New(ctx)
	if err != nil {
		return nil, nil, err
	}
	tracerProvider := trace.NewTracerProvider(
		trace.WithBatcher(traceExporter),
		trace.WithResource(res),
		// The data generator is polled constantly (frontend dashboard, health checks), which
		// floods the trace view with standalone single-span traces. Never-sample root spans so
		// that contextless polls are dropped, but still record spans that are part of an agent's
		// distributed trace (i.e. arrive with a sampled remote parent via propagated traceparent).
		trace.WithSampler(trace.ParentBased(trace.NeverSample())),
	)
	otel.SetTracerProvider(tracerProvider)
	otel.SetTextMapPropagator(propagation.NewCompositeTextMapPropagator(
		propagation.TraceContext{},
		propagation.Baggage{},
	))

	logExporter, err := otlploggrpc.New(ctx)
	if err != nil {
		return nil, nil, errors.Join(tracerProvider.Shutdown(ctx), err)
	}
	loggerProvider := log.NewLoggerProvider(
		log.WithProcessor(log.NewBatchProcessor(logExporter)),
		log.WithResource(res),
	)
	global.SetLoggerProvider(loggerProvider)

	logger := slog.New(otelslog.NewHandler(serviceName, otelslog.WithLoggerProvider(loggerProvider)))
	return func(ctx context.Context) error {
		return errors.Join(
			loggerProvider.Shutdown(ctx),
			tracerProvider.Shutdown(ctx),
		)
	}, logger, nil
}

func otelExporterConfigured() bool {
	return os.Getenv("OTEL_EXPORTER_OTLP_ENDPOINT") != "" ||
		os.Getenv("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT") != "" ||
		os.Getenv("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT") != ""
}

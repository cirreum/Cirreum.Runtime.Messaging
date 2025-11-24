# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

### Build Commands
```bash
# Build the project
dotnet build

# Build in Release mode
dotnet build -c Release

# Clean and rebuild
dotnet clean && dotnet build

# Pack NuGet package
dotnet pack -c Release
```

### Development Commands
```bash
# Restore dependencies
dotnet restore

# Watch mode for development
dotnet watch build
```

## High-Level Architecture

### Core Purpose
Cirreum.Runtime.Messaging is a distributed messaging library that provides:
- Message publishing with both synchronous and asynchronous delivery modes
- Advanced batching system with dynamic sizing and priority management
- Azure Service Bus integration
- Comprehensive observability through OpenTelemetry

### Key Architectural Components

#### 1. Message Publisher System
- **DefaultTransportPublisher** (src/Cirreum.Runtime.Messaging/DefaultTransportPublisher.cs) - Central message publisher supporting both direct and background delivery
- Implements `IDistributedMessagePublisher` interface
- Supports queue-based events and topic-based notifications via message attributes

#### 2. Background Batching System
The batching system (src/Cirreum.Runtime.Messaging/Batching/) provides sophisticated message batching:
- **DefaultBatchProcessor** - Background service managing batched delivery
- **BatchScheduler** - Dynamic batch sizing based on time profiles (peak/off-peak)
- **BatchCircuitBreaker** - Resilience pattern for handling failures
- **MessagePrioritizer** - Priority-based queueing with automatic promotion

#### 3. Observability Infrastructure
- **DefaultMessagingMetricsService** (src/Cirreum.Runtime.Messaging/Metrics/) - OpenTelemetry metrics collection
- Tracks message lifecycle: received, queued, delivered, failed
- Queue depth monitoring with configurable alerting
- Distributed tracing support via ActivitySource

### Configuration Architecture
The library uses hierarchical configuration through Microsoft.Extensions.Configuration:
- Root configuration section: `DistributedMessaging`
- Background delivery options: `DistributedMessaging:BackgroundDelivery`
- Metrics configuration: `DistributedMessaging:Metrics`
- Time-based batching profiles for load management

### Service Registration Pattern
Extension methods in src/Cirreum.Runtime.Messaging/Extensions/Hosting/ provide clean DI registration:
```csharp
builder.AddDistributedMessaging()
       .AddDistributedMessagingMetrics()
       .AddAzureServiceBusProvider()
```

### Key Design Patterns
1. **Options Pattern** - All configuration uses strongly-typed options with validation
2. **Background Service Pattern** - IHostedService for background processing
3. **Circuit Breaker Pattern** - Fault tolerance for message delivery
4. **Repository Pattern** - Message registry for type management

## Important Dependencies
- Targets .NET 10.0 with latest C# features and nullable reference types
- Depends on Cirreum ecosystem packages (Core, Messaging.Azure, Runtime.ServiceProvider)
- Uses Microsoft.Extensions.* for hosting, DI, and configuration
- OpenTelemetry for observability

## Configuration Files
- See CONFIGURATION_GUIDE.md for detailed JSON configuration examples
- Supports environment-specific settings through standard .NET configuration providers
- Validation attributes ensure configuration correctness at startup
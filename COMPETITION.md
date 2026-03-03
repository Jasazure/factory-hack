# Agentic AI Competition Entry

## Intelligent Predictive Maintenance — Multi-Agent Workflow

---

### 1. What does it do?

It monitors factory machines, diagnoses faults, and kicks off the full maintenance response — automatically, end-to-end.

Five agents work in sequence:

| Agent | What it does |
|---|---|
| **Anomaly Classification** | Reads live sensor telemetry, compares against thresholds, flags warnings and critical conditions |
| **Fault Diagnosis** | Identifies the most likely root cause using telemetry data and a structured machine knowledge base |
| **Repair Planner** | Generates a work order — repair tasks, required skills, parts needed, priority |
| **Maintenance Scheduler** | Proposes a maintenance window based on risk score, technician availability, and production constraints |
| **Parts Ordering** | Checks inventory; raises supplier orders for any parts below threshold or required for the repair |

A machine sensor fires → five agents later, there's a scheduled work order, a parts order, and a technician assigned. No human had to chase anything down.

---

### 2. How does it decide what to do?

Every decision is grounded in real data — no guessing.

- **Telemetry + thresholds** from Cosmos DB tell the anomaly agent whether a reading is normal, warning, or critical.
- **A knowledge base** (Foundry IQ / Azure AI Search) gives the fault diagnosis agent documented fault types, likely causes, and historical patterns to reference — it is explicitly instructed not to answer from its own knowledge.
- **Deterministic mappings** (fault type → required skills, fault type → required parts) ensure the repair planner pulls the right resources every time, before the LLM writes the tasks.
- **Live Cosmos DB queries** feed the scheduler and parts orderer with actual technician availability, shift data, and inventory levels.
- **Persistent memory** (agent threads) lets the scheduler and orderer retain context across sessions, so repeat faults benefit from prior decisions.

The LLM handles language and reasoning; the data handles facts.

---

### 3. Why is this valuable?

Unplanned downtime is one of the most expensive problems in manufacturing. A single unplanned stoppage can cost tens of thousands per hour in lost throughput, scrap, and recovery time.

Today that process is manual: a technician reads an alert, consults a manual, tracks down a colleague with the right skills, checks the parts room, and raises a ticket — often taking hours. The outcome depends heavily on who happens to be on shift and how much they know.

This system compresses that coordination to seconds, consistently, every time:

- **Faster response** — from sensor alert to actionable work order in one automated pipeline
- **Consistent diagnosis** — grounded in the same knowledge base, not tribal knowledge
- **Less coordination overhead** — technicians arrive knowing what to do, with parts ready
- **Observable and auditable** — every agent decision is traceable via Application Insights

The agents don't replace the technician. They remove the coordination work so the technician can focus on the actual fix.

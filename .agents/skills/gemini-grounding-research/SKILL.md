---
description: Use Gemini Grounding for research when sequential thinking indicates need for broader context. Only use when really necessary due to cost. Provide precise, meaningful queries with essential context.
keywords: [gemini, grounding, research, context, diagnosis, troubleshooting, costly]
---

# Gemini Grounding Research Skill

## Purpose

Use the `gemini-grounding` MCP tool for targeted research when troubleshooting complex issues that require broader context beyond local knowledge. This tool is **COSTLY** and should only be used when:

- Sequential thinking returns `revisesThought` (indicating insufficient information)
- The problem requires understanding of broader patterns or external knowledge
- Local logs, code, and documentation are insufficient
- Need to understand industry-standard solutions or common patterns

## ⚠️ Cost Awareness

**IMPORTANT:** This tool has significant API costs. Only use when absolutely necessary.

**Cost Triggers:**
- ✅ Sequential thinking shows `revisesThought`
- ✅ Need broader context than local codebase provides
- ✅ Problem involves external systems, standards, or patterns
- ❌ Simple debugging or code reading
- ❌ When local documentation is sufficient
- ❌ Routine troubleshooting

## Usage Guidelines

### When to Use

**Use ONLY when:**
1. **Sequential thinking fails** - Tool returns `revisesThought`
2. **Need external knowledge** - Problem involves standards, patterns, or external systems
3. **Broader context required** - Local codebase insufficient for diagnosis
4. **Industry patterns needed** - Understanding common solutions or anti-patterns

**Do NOT use for:**
- Reading local code or logs
- Simple syntax errors
- Configuration typos
- Basic debugging

### Query Construction

**Rule: Precise + Essential Context**

**❌ BAD (Too much info):**
```javascript
{
  "query": "Why is my API timing out? I have a Node.js application with Express server running on port 3000, using PostgreSQL database with connection pool size of 10, deployed on Kubernetes with 3 replicas, using nginx as reverse proxy, and the error logs show 'connection timeout after 30 seconds' and I have 100 concurrent users and the CPU is at 85% and memory at 90% and disk I/O is high and there are 50 database connections active and..."
}
```

**✅ GOOD (Precise + Essential):**
```javascript
{
  "query": "Database connection pool exhaustion symptoms and diagnosis",
  "context": "PostgreSQL timeouts at 30s, concurrent requests > 5, pool size recently changed from 20 to 10",
  "maxResults": 3
}
```

### Query Structure

**Essential Elements:**
1. **Core Problem** - What specifically is failing?
2. **Key Symptoms** - Measurable indicators (timeouts, error counts, resource usage)
3. **Recent Changes** - What changed that might cause this
4. **Architecture Context** - Technology stack involved

**Template:**
```javascript
{
  "query": "[CORE_PROBLEM] [KEY_SYMPTOMS]",
  "context": "[ESSENTIAL_CONTEXT]: [MEASURABLE_INDICATORS], [RECENT_CHANGES]",
  "maxResults": 3  // Keep low to control costs
}
```

### Integration with Rethought Troubleshooting

**Workflow:**

```
1. Problem occurs 6+ times
2. Use sequential-thinking (totalThoughts: 12)
3. IF result = revisesThought:
   → Use gemini-grounding for broader context
   → Query: "Problem symptoms and diagnosis"
   → Context: "Key measurable indicators, recent changes"
4. Incorporate results back into diagnosis
5. Continue with binary isolation, delta analysis
```

## Examples

### Example 1: Database Connection Issues

**Trigger:** Sequential thinking shows `revisesThought` about database patterns

```javascript
{
  "query": "Database connection pool exhaustion diagnosis",
  "context": "PostgreSQL timeouts at 30s, concurrent requests > 5, pool size changed from 20 to 10",
  "maxResults": 3
}
```

**Expected Results:**
- Common symptoms of pool exhaustion
- Diagnostic steps
- Industry-standard solutions

### Example 2: API Gateway Timeouts

**Trigger:** Need to understand gateway patterns beyond local code

```javascript
{
  "query": "API gateway timeout patterns and causes",
  "context": "Requests timeout at 30s, gateway CPU 95%, downstream services healthy",
  "maxResults": 3
}
```

### Example 3: Memory Leak Diagnosis

**Trigger:** Sequential thinking indicates need for broader memory management knowledge

```javascript
{
  "query": "Node.js memory leak diagnosis patterns",
  "context": "RSS memory grows 200MB/hour, heap usage stable, no obvious leaks in code review",
  "maxResults": 3
}
```

## Cost Optimization

### Minimize API Calls

**Strategy 1: Exhaust Local Resources First**
```
1. Check local documentation
2. Review code and logs
3. Use sequential thinking
4. Only then use gemini-grounding
```

**Strategy 2: Batch Related Questions**
```javascript
// Instead of multiple calls:
{
  "query": "Database timeout causes"
}
{
  "query": "Connection pool sizing"
}
{
  "query": "PostgreSQL performance tuning"
}

// Use one comprehensive query:
{
  "query": "Database connection pool sizing and timeout diagnosis",
  "context": "PostgreSQL timeouts at 30s, concurrent requests > 5, pool size 10",
  "maxResults": 5
}
```

### Result Processing

**Extract Key Insights:**
- Focus on **actionable** information
- Ignore generic advice
- Look for **measurable** diagnostics
- Identify **industry patterns**

**Example Processing:**
```javascript
// Gemini result: "Connection pools should be sized at 2-4x concurrent users"
// Action: Check current pool size vs. actual concurrent users
// Result: Pool size 10, concurrent users 50 → Increase pool to 20-40
```

## Integration Examples

### With Sequential Thinking

```javascript
// Step 1: Sequential thinking
{
  "totalThoughts": 12,
  "thoughts": [
    {"thought": "API timeouts 6+ times/week", "type": "observation"},
    {"thought": "Need broader context on timeout patterns", "type": "planning"}
  ]
}

// Step 2: If revisesThought returned
{
  "query": "API timeout diagnosis patterns",
  "context": "Timeouts at 30s, database involved, connection pooling suspected",
  "maxResults": 3
}

// Step 3: Use results in next sequential thinking round
{
  "totalThoughts": 8,
  "thoughts": [
    {"thought": "Gemini results suggest connection pool exhaustion", "type": "observation"},
    {"thought": "Check pool size vs concurrent connections", "type": "action"}
  ]
}
```

### With Rethought Troubleshooting

**Phase Integration:**

```
Phase 1: Evidence-First ✓ (logs collected)
Phase 2: Binary Isolation → revisesThought (need more context)
Phase 3: Use gemini-grounding for broader patterns
Phase 4: Delta Analysis ✓ (with new context)
Phase 5: Induced Failure ✓ (test pool exhaustion)
```

## Error Handling

### If Query Too Broad
```javascript
// Bad: Too vague
{"query": "Why does my app crash?"}

// Good: Specific symptoms
{"query": "Node.js heap out of memory crash diagnosis"}
```

### If Results Irrelevant
- **Refine query** with more specific symptoms
- **Add context** about technology stack
- **Reduce maxResults** to get more focused results

### Cost Monitoring
- Track usage patterns
- Review if each call was necessary
- Consider alternatives (documentation, code search) first

## Best Practices

### Query Quality
- **Specific problem** + **measurable symptoms**
- **Essential context** only (no fluff)
- **Technology stack** mentioned
- **Recent changes** included

### Cost Control
- **maxResults: 3-5** (not 10+)
- **One comprehensive query** vs multiple small ones
- **Verify necessity** before calling
- **Use results efficiently** (don't waste the expensive call)

### Result Utilization
- **Extract actionable insights**
- **Map to local context**
- **Validate against evidence**
- **Incorporate into systematic diagnosis**

## Summary

**Gemini Grounding = Expensive Research Tool**

Use only when:
- Sequential thinking shows `revisesThought`
- Need broader context than local knowledge
- Problem involves external patterns or standards

**Query Formula:**
```
"Problem symptoms and diagnosis" + "Key measurable indicators, recent changes"
```

**Cost Mindset:** Each call costs money. Make it count.
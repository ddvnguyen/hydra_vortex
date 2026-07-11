# ask-followup-question Skill

## Purpose
Guide for using the `ask_followup_question` tool to present structured multiple-choice clarification questions with options A, B, C, Other and a text input field for custom responses.

## When to Use
- You need clarification from the user before proceeding
- There are multiple valid approaches and you need user preference
- Decision points require user input
- You need to confirm user's choice before acting

## Format

### Structure
Always present questions in this format:

```
[Question context and description]

Option: A | B | C | Other

[Optional: Brief description of each option]
```

### Tool Usage
```xml
<ask_followup_question>
<question>[Your question text with Option: A | B | C | Other format]</question>
<options>
  ["A) [Option A description]", "B) [Option B description]", "C) [Option C description]", "Other) [Custom option description]"]
</options>
</ask_followup_question>
```

## Examples

### Example 1: Approach Selection
```xml
<ask_followup_question>
<question>We need to decide on the monitoring stack approach.

Option: A | B | C | Other

A) Use Docker Compose for simple setup
B) Use Podman for rootless containers  
C) Use Kubernetes for scalability
Other) Specify a different orchestration method</question>
<options>
  ["A) Use Docker Compose for simple setup", "B) Use Podman for rootless containers", "C) Use Kubernetes for scalability", "Other) Specify a different orchestration method"]
</options>
</ask_followup_question>
```

### Example 2: Implementation Priority
```xml
<ask_followup_question>
<question>Which component should we implement first?

Option: A | B | C | Other

A) Prometheus configuration for metric collection
B) Grafana dashboards for visualization
C) Alerting rules for notification
Other) Different priority order</question>
<options>
  ["A) Prometheus configuration for metric collection", "B) Grafana dashboards for visualization", "C) Alerting rules for notification", "Other) Different priority order"]
</options>
</ask_followup_question>
```

## Handling "Other" Option

**Important limitation**: The `ask_followup_question` tool only provides clickable option buttons and does NOT have a text input field. When user selects "Other", they cannot type custom input in the same question.

### Workaround: Two-Step Workflow

When "Other" is selected, ask a follow-up question WITHOUT options to prompt text input:

```
Step 1: ask_followup_question with options A, B, C, Other
Step 2: User selects "Other"
Step 3: ask_followup_question with open question (no options array) to get text input
```

Example:
```xml
<!-- Step 1: Present options -->
<ask_followup_question>
<question>Choose an approach:

Option: A | B | C | Other

A) Option A description
B) Option B description  
C) Option C description
Other) Custom alternative</question>
<options>["A) Option A", "B) Option B", "C) Option C", "Other) Custom alternative"]</options>
</ask_followup_question>

<!-- Step 2 (if Other selected): Get text input -->
<ask_followup_question>
<question>Please describe your custom alternative:</question>
</ask_followup_question>
```

## Confirming User Selection

After the user responds, confirm their choice:

```
You selected: [Option description]

I will now proceed with [action based on selection].
```

## Notes

- Limit to 3 concrete options (A, B, C) plus "Other"
- Options should cover the most common or recommended choices
- The "Other" option should have a descriptive placeholder
- Keep question text concise but informative
- "Other" selection requires a follow-up question for text input (tool has no built-in text field)

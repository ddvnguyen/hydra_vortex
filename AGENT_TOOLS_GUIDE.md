# Coding Agent Tools: question + todowrite Guide

## The Pattern

Use `question` when you need user decisions before proceeding. Use `todowrite` to track your work. **Always use both together** — never just one alone.

---

## 1. Start with todowrite (always)

Before doing ANY work, create a todo list. This makes progress visible and prevents tasks from being forgotten.

```
todowrite(todos=[
  {content: "Review Coordinator service", status: "pending", priority: "high"},
  {content: "Review Agent service", status: "pending", priority: "high"},
  {content: "Review Store service", status: "pending", priority: "high"}
])
```

**Rules:**
- Exactly ONE item at a time must be `in_progress`
- Mark `completed` only when the task is actually done (not started, not half-done)
- Use priority levels: high / medium / low
- Update status IMMEDIATELY when moving between tasks

---

## 2. Ask question when stuck on a decision

When you encounter ambiguity or need user input, use `question`. Don't guess — ask.

```
question(questions=[{
  "question": "The local branch is 3 commits behind the remote with uncommitted changes. What should I do?",
  "header": "Sync strategy",
  "options": [
    {"label": "Push + Pull", "description": "Push my local changes first, then pull from remote"},
    {"label": "Stash and pull", "description": "Stash my changes temporarily, pull from remote, re-apply stash"},
    {"label": "Force reset", "description": "Discard all local changes and reset to remote"}
  ],
  "multiple": false
}])
```

**Rules:**
- Always include `header` (short label) and `question` (clear context)
- Provide 2-4 options with descriptions — don't make users guess
- Set `multiple: false` for single-choice decisions
- Only use `multiple: true` when user genuinely needs to select multiple things
- Include `**Status:**` in question text if referring to prior work

---

## 3. Handle "Other" option

If the user selects "Other", ask a follow-up question with empty options array to get their custom input:

```
question(questions=[{
  "question": "You selected Other. What should I do instead?",
  "header": "Custom action",
  "options": []
}])
```

---

## Good vs Bad Examples

### BAD: Guessing without asking
```python
# Don't just assume what the user wants
git push origin main
git pull origin main
```

### GOOD: Asking before irreversible actions
```
question(questions=[{
  "question": "There are uncommitted changes on 8 files. Should I commit them before pulling from remote?",
  "header": "Uncommitted changes",
  "options": [
    {"label": "Commit first", "description": "Save my local changes, then pull"},
    {"label": "Stash and pull", "description": "Temporarily stash changes, pull, then re-apply"},
    {"label": "Don't touch them", "description": "Ignore the uncommitted changes"}
  ],
  "multiple": false
}])
```

---

## Working Together: The Full Loop

```
1. todowrite — create task list (all pending)
2. Set first item to in_progress
3. Do the work
4. If decision needed → question()
5. On user response → update todo status, continue work
6. Mark tasks completed as you go
```

---

## Common Mistakes

1. **Using todowrite without updating statuses** — stale lists are useless
2. **Asking questions when you have enough info** — if the answer is obvious from code, just do it
3. **Not using todowrite at all** — even simple tasks benefit from tracking
4. **Creating too many todos** — keep the list to 5-8 items max, break big tasks into sub-tasks
5. **Asking questions with vague options** — always explain what each choice means

---

## Quick Reference

| Tool | Purpose | When to use |
|------|---------|-------------|
| `todowrite` | Track progress | Always, at the start of any work |
| `question` | Get decisions | When user input is needed before proceeding |
| `question(questions=[[]])` | Custom input | When "Other" option selected or user needs to describe something |

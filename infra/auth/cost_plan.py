"""Nonsecret resource-group spending-alert planning; deliberately no apply API."""
from datetime import date
import json
import os
from pathlib import Path
import re

from deploy import DeploymentError

FIELDS = ("amount", "timeGrain", "startDate", "endDate", "thresholds")


def budget_plan(scope):
    """Validate explicit operator choices without claiming billing eligibility.

    The compact plan supports recurring calendar periods and Actual/Gte alerts.
    It is not an ARM payload or a budget/notification deployment entrypoint.
    """
    filename = os.environ.get("AUTH_BUDGET_PLAN_FILE")
    inputs = json.loads(Path(filename).read_text(encoding="utf-8")) if filename else {}
    allowed = {*FIELDS, "contactEmails", "contactGroups", "scope"}
    if not isinstance(inputs, dict) or set(inputs) - allowed:
        raise DeploymentError("Budget plan must be an object with documented fields only")
    if "scope" in inputs and (not isinstance(inputs["scope"], str) or inputs["scope"].lower() != scope.lower()):
        raise DeploymentError("Budget plan scope must be the approved resource group")
    missing = [field for field in FIELDS if field not in inputs]
    emails = inputs.get("contactEmails", [])
    groups = inputs.get("contactGroups", [])
    if not isinstance(emails, list) or not all(isinstance(email, str) and re.fullmatch(r"[^\s@]+@[^\s@]+\.[^\s@]+", email) for email in emails):
        raise DeploymentError("Budget contactEmails must contain explicit email addresses")
    group_pattern = re.escape(scope) + r"/providers/Microsoft\.Insights/actionGroups/[A-Za-z0-9_.()-]+"
    if not isinstance(groups, list) or not all(isinstance(group, str) and re.fullmatch(group_pattern, group, re.IGNORECASE) for group in groups):
        raise DeploymentError("Budget contactGroups must contain existing approved resource-group action-group IDs")
    if not emails and not groups:
        missing.append("contactEmails or contactGroups (existing approved action group)")
    if "amount" in inputs:
        if type(inputs["amount"]) is not int or inputs["amount"] <= 0:
            raise DeploymentError("Budget amount must be a positive integer in the selected billing currency")
    if "timeGrain" in inputs and inputs["timeGrain"] not in ("Monthly", "Quarterly", "Annually"):
        raise DeploymentError("Budget timeGrain must be Monthly, Quarterly or Annually")
    dates = {}
    for field in ("startDate", "endDate"):
        if field in inputs:
            value = inputs[field]
            try:
                if not isinstance(value, str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", value):
                    raise ValueError()
                dates[field] = date.fromisoformat(value)
            except ValueError as error:
                raise DeploymentError(f"Budget {field} must be a calendar date YYYY-MM-DD") from error
    if "startDate" in dates and dates["startDate"].day != 1:
        raise DeploymentError("Budget startDate must be the first day of a month")
    if len(dates) == 2 and dates["endDate"] <= dates["startDate"]:
        raise DeploymentError("Budget endDate must follow startDate")
    if "thresholds" in inputs:
        thresholds = inputs["thresholds"]
        if (not isinstance(thresholds, list) or not 1 <= len(thresholds) <= 5 or
                not all(type(value) is int and 0 < value <= 1000 for value in thresholds) or
                len(set(thresholds)) != len(thresholds)):
            raise DeploymentError("Budget thresholds must contain 1-5 distinct integer percentages above 0 and at most 1000")
    return {"scope": scope, "schema": "Microsoft.Consumption/budgets@2024-08-01",
            "inputs": inputs, "missingInputs": missing,
            "billingEligibility": "unknown", "readyToApply": False,
            "preApplyGates": ["Verify selected subscription billing offer supports resource-group budgets",
                              "Verify currency, date horizon/current-period constraints and recipient/action-group ownership with the operator",
                              "Review a separately authorized budget deployment and notification plan"],
            "notification": {"thresholdType": "Actual", "operator": "GreaterThanOrEqualTo"},
            "hardSpendingCap": False}

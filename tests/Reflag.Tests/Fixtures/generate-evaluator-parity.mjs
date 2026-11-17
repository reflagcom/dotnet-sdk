// Generates evaluator-v2-parity.json from the published JavaScript evaluator, so that the
// .NET port is checked against the reference implementation rather than a re-derivation.
//
//   mkdir /tmp/evaluator-v2 && cd /tmp/evaluator-v2 && npm init -y && npm i @reflag/flag-evaluation@2.0.0
//   NODE_PATH=/tmp/evaluator-v2/node_modules node generate-evaluator-parity.mjs > evaluator-v2-parity.json
//
// Relative-date operators use dates far from "now" so the expected results stay stable.
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { newEvaluator, hashInt } = require("@reflag/flag-evaluation");

const yes = { type: "constant", value: true };
const no = { type: "constant", value: false };
const ctx = (field, operator, values) => ({ type: "context", field, operator, ...(values ? { values } : {}) });
const group = (operator, ...filters) => ({ type: "group", operator, filters });
const negate = (filter) => ({ type: "negation", filter });
const flagRef = (key, variantKey = "on") => ({ type: "flag", key, variantKey });
const segmentRef = (key) => ({ type: "segment", key });
const variant = (variantKey) => ({ type: "variant", variantKey });
const rule = (id, filter, result = variant("on")) => ({ id, filter, result });
const boolFlag = (key, ...filters) => ({
  key,
  sourceVersionId: `version-${key}`,
  variants: { on: true, off: false },
  rules: [...filters.map((filter, index) => rule(`rule-${index}`, filter)), rule("default", yes, variant("off"))],
});

const operators = ["IS", "IS_NOT", "ANY_OF", "NOT_ANY_OF", "CONTAINS", "NOT_CONTAINS", "GT", "LT", "AFTER", "BEFORE",
  "DATE_AFTER", "DATE_BEFORE", "SET", "NOT_SET", "IS_TRUE", "IS_FALSE"];
const operatorValues = {
  IS: ["Acme"], IS_NOT: ["Acme"], ANY_OF: ["acme", "beta", "42"], NOT_ANY_OF: ["acme", "42"],
  CONTAINS: ["cm"], NOT_CONTAINS: ["cm"], GT: ["10"], LT: ["10"], AFTER: ["1"], BEFORE: ["1"],
  DATE_AFTER: ["2020-01-01"], DATE_BEFORE: ["2020-01-01T00:00:00Z"], SET: undefined, NOT_SET: undefined,
  IS_TRUE: undefined, IS_FALSE: undefined,
};
const operatorFields = ["name", "tags", "count", "joined", "flag", "empty", "missing", "emptyList", "nested.deep"];

const suites = [];

// 1. Every operator against scalar, array, empty and missing fields.
suites.push({
  name: "operators",
  collection: {
    flags: operators.flatMap((operator) => operatorFields.map((field) =>
      boolFlag(`${operator}:${field}`, ctx(`company.${field}`, operator, operatorValues[operator])))),
    segments: [],
  },
  contexts: [
    { company: { name: "Acme", tags: ["acme", "x"], count: 42, joined: "2001-02-03", flag: true, empty: "", emptyList: [], nested: { deep: "ACME inc" } } },
    { company: { name: "acme", tags: ["Acme"], count: "7.5", joined: "not a date", flag: "false", empty: null, emptyList: [null, 1, { a: 1 }], nested: { deep: 1 } } },
    { company: { name: " 12 ", tags: [], count: "", joined: "2099-12-31T23:59:59Z", flag: false, nested: {} } },
    { company: {} },
    {},
  ],
});

// 2. Invalid comparison values in definitions, and missing values lists.
suites.push({
  name: "invalid-comparisons",
  collection: {
    flags: [
      boolFlag("gt-bad-target", ctx("user.age", "GT", ["many"])),
      boolFlag("lt-no-values", ctx("user.age", "LT")),
      boolFlag("date-bad-target", ctx("user.joined", "DATE_AFTER", ["soon"])),
      boolFlag("date-no-values", ctx("user.joined", "DATE_BEFORE")),
      boolFlag("after-bad-days", ctx("user.joined", "AFTER", ["x"])),
      boolFlag("is-no-values", ctx("user.age", "IS")),
      boolFlag("is-not-no-values", ctx("user.age", "IS_NOT")),
      boolFlag("contains-no-values", ctx("user.age", "CONTAINS")),
      boolFlag("array-contains-no-values", ctx("user.roles", "CONTAINS")),
      boolFlag("errors-then-grant", ctx("user.age", "GT", ["x"]), ctx("user.id", "IS", ["u1"])),
    ],
    segments: [],
  },
  contexts: [{ user: { id: "u1", age: 30, joined: "2010-01-01", roles: ["a"] } }, { user: { id: "u2", age: "old", joined: "?", roles: [] } }],
});

// 3. Rollouts and percentage distributions across many identifiers.
const ids = Array.from({ length: 60 }, (_, index) => `company-${index}`);
suites.push({
  name: "distributions",
  collection: {
    flags: [
      boolFlag("rollout-half", { type: "rolloutPercentage", key: "rollout-half", partialRolloutAttribute: "company.id", partialRolloutThreshold: 50000 }),
      boolFlag("rollout-array", { type: "rolloutPercentage", key: "rollout-array", partialRolloutAttribute: "company.tags", partialRolloutThreshold: 50000 }),
      {
        key: "model",
        sourceVersionId: "version-model",
        variants: { small: { model: "small", tokens: 1000 }, large: { model: "large", tokens: 8000 }, none: null, list: [1, "two", false] },
        rules: [
          rule("split", yes, {
            type: "percentageDistribution", key: "model.split", attribute: "company.id",
            allocations: [
              { percentage: 25, destination: variant("small") },
              { percentage: 25, destination: variant("large") },
              { percentage: 20, destination: { type: "nextRule" } },
              { percentage: 0, destination: variant("none") },
              { percentage: 30, destination: variant("list") },
            ],
          }),
          rule("default", yes, variant("none")),
        ],
      },
      {
        key: "thousandths",
        sourceVersionId: "version-thousandths",
        variants: { a: "a", b: "b" },
        rules: [
          rule("split", yes, { type: "percentageDistribution", key: "t", attribute: "company.id",
            allocations: [{ percentage: 33.333, destination: variant("a") }, { percentage: 66.667, destination: variant("b") }] }),
          rule("default", yes, variant("a")),
        ],
      },
      {
        key: "bad-allocation-sum",
        sourceVersionId: "version-bad",
        variants: { a: 1 },
        rules: [
          rule("split", yes, { type: "percentageDistribution", key: "bad", attribute: "company.id",
            allocations: [{ percentage: 50, destination: variant("a") }] }),
          rule("default", yes, variant("a")),
        ],
      },
      {
        key: "missing-attribute",
        sourceVersionId: "version-missing",
        variants: { a: "a", b: "b" },
        rules: [
          rule("split", yes, { type: "percentageDistribution", key: "m", attribute: "company.plan",
            allocations: [{ percentage: 100, destination: variant("b") }] }),
          rule("default", yes, variant("a")),
        ],
      },
      {
        key: "array-attribute",
        sourceVersionId: "version-array",
        variants: { a: "a", b: "b" },
        rules: [
          rule("split", yes, { type: "percentageDistribution", key: "arr", attribute: "company.tags",
            allocations: [{ percentage: 100, destination: variant("b") }] }),
          rule("default", yes, variant("a")),
        ],
      },
      {
        key: "unknown-variant",
        sourceVersionId: "version-unknown",
        variants: { a: "a" },
        rules: [rule("broken", yes, variant("ghost")), rule("default", yes, variant("a"))],
      },
      {
        key: "no-catch-all",
        sourceVersionId: "version-no-catch-all",
        variants: { a: "a" },
        rules: [rule("only", ctx("company.id", "IS", ["company-1"]), variant("a"))],
      },
    ],
    segments: [],
  },
  contexts: [...ids.map((id) => ({ company: { id, tags: ["x"] } })), { company: { tags: ["x"] } }],
});

// 4. Flag and segment references, negation and propagated diagnostics.
suites.push({
  name: "references",
  collection: {
    flags: [
      boolFlag("base", ctx("company.id", "ANY_OF", ["acme"])),
      boolFlag("uncertain", ctx("company.credits", "GT", ["5"])),
      boolFlag("uncertain-then-grant", ctx("company.credits", "GT", ["5"]), ctx("company.id", "IS", ["acme"])),
      boolFlag("depends-on-base", flagRef("base")),
      boolFlag("depends-on-base-off", flagRef("base", "off")),
      boolFlag("not-uncertain", negate(flagRef("uncertain"))),
      boolFlag("not-uncertain-grant", negate(flagRef("uncertain-then-grant"))),
      boolFlag("uses-segment", segmentRef("enterprise")),
      boolFlag("not-segment", negate(segmentRef("enterprise"))),
      boolFlag("uses-nested-segment", segmentRef("nested")),
      boolFlag("or-segment-or-base", group("or", segmentRef("broken"), flagRef("base"))),
      boolFlag("and-group", group("and", flagRef("base"), segmentRef("enterprise"), negate(no))),
      {
        key: "multivariate-ref",
        sourceVersionId: "version-mv",
        variants: { quality: { model: "large" }, cheap: { model: "small" } },
        rules: [rule("q", flagRef("base"), variant("quality")), rule("default", yes, variant("cheap"))],
      },
      boolFlag("depends-on-mv", flagRef("multivariate-ref", "quality")),
    ],
    segments: [
      { key: "enterprise", filter: group("and", ctx("company.plan", "IS", ["enterprise"]), ctx("company.seats", "GT", ["100"])) },
      { key: "nested", filter: group("or", segmentRef("enterprise"), flagRef("base")) },
      { key: "broken", filter: ctx("company.credits", "LT", ["5"]) },
    ],
  },
  contexts: [
    { company: { id: "acme", plan: "enterprise", seats: 500, credits: "invalid" } },
    { company: { id: "acme", plan: "enterprise", seats: "lots", credits: 10 } },
    { company: { id: "beta", plan: "free", seats: 5, credits: 1 } },
    { company: { id: "beta" } },
    {},
  ],
});

const output = {
  generatedBy: "@reflag/flag-evaluation@2.0.0",
  hashes: ids.concat(["", "x", "ünïcødé", "company.id.42"]).map((input) => ({ input: `split.${input}`, value: hashInt(`split.${input}`) })),
  suites: suites.map(({ name, collection, contexts }) => {
    const evaluator = newEvaluator(collection);
    return {
      name,
      collection,
      cases: contexts.map((context) => ({ context, flags: evaluator.evaluateAll(context).flags })),
    };
  }),
};

process.stdout.write(JSON.stringify(output) + "\n");

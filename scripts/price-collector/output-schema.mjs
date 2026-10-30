// Validates one collector output file against ./output.schema.json (issue #926, parent #817).
//
// **Each file is validated on its own**, which is the whole reason there is a file per work item: a
// file that is missing, unreadable or wrong loses only its own search, and every finished file is
// still submitted. The validation is deliberately stricter than the API in the places where a wrong
// value would be *accepted* - a barcode with no scope, two rows claiming one supplier product id, a
// search the page cap stopped calling itself complete - because the API would take those as a price
// or as a listing that is no longer stocked (#937).
//
// The checker is a small, dependency-free reader of the subset of JSON Schema the document uses
// (`type`, `required`, `properties`, `additionalProperties: false`, `enum`, `minimum`/`maximum`,
// `exclusiveMinimum`, `minLength`/`maxLength`, `items` and `format: date-time`). It never coerces a
// value and never fills one in: a file says what it says.
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

/** The schema document, as shipped beside this module. */
export const SCHEMA = JSON.parse(readFileSync(join(here, 'output.schema.json'), 'utf8'));

const isPlainObject = (value) => typeof value === 'object' && value !== null && !Array.isArray(value);

/** Drops keys explicitly set to `undefined`, which `JSON.parse` never produces but a caller may. */
function present(value) {
  if (!isPlainObject(value)) return value;
  return Object.fromEntries(Object.entries(value).filter(([, entry]) => entry !== undefined));
}

function typeMatches(expected, value) {
  switch (expected) {
    case 'object':
      return isPlainObject(value);
    case 'array':
      return Array.isArray(value);
    case 'integer':
      return typeof value === 'number' && Number.isInteger(value);
    case 'number':
      return typeof value === 'number' && Number.isFinite(value);
    case 'string':
      return typeof value === 'string';
    case 'boolean':
      return typeof value === 'boolean';
    default:
      throw new Error(`The schema declares a type this checker does not read: ${expected}.`);
  }
}

const typeLabel = (type) => (type === 'integer' ? 'a whole number' : `a ${type}`);

/** The scalar constraints a schema may declare, beyond its own type and nesting. */
function checkConstraints(schema, value, path, problems) {
  if (Array.isArray(schema.enum) && !schema.enum.includes(value)) {
    problems.push(`${path} must be one of: ${schema.enum.join(', ')}.`);
  }
  if (typeof schema.minimum === 'number' && value < schema.minimum) {
    problems.push(`${path} must be ${schema.minimum} or more.`);
  }
  if (typeof schema.maximum === 'number' && value > schema.maximum) {
    problems.push(`${path} must be ${schema.maximum} or less.`);
  }
  if (typeof schema.exclusiveMinimum === 'number' && value <= schema.exclusiveMinimum) {
    problems.push(`${path} must be above ${schema.exclusiveMinimum}.`);
  }
  if (typeof schema.minLength === 'number' && value.length < schema.minLength) {
    problems.push(`${path} must hold at least ${schema.minLength} character(s).`);
  }
  if (typeof schema.maxLength === 'number' && value.length > schema.maxLength) {
    problems.push(`${path} must hold at most ${schema.maxLength} characters.`);
  }
  if (schema.format === 'date-time' && Number.isNaN(Date.parse(value))) {
    problems.push(`${path} must be an instant a date can be read from.`);
  }
}

function checkValue(schema, value, path, problems) {
  if (!typeMatches(schema.type, value)) {
    problems.push(`${path} must be ${typeLabel(schema.type)}.`);
    return;
  }

  checkConstraints(schema, value, path, problems);

  if (schema.type === 'object') checkObject(schema, value, path, problems);
  if (schema.type === 'array' && schema.items !== undefined) {
    value.forEach((entry, index) => checkValue(schema.items, present(entry), `${path}[${index}]`, problems));
  }
}

function checkObject(schema, value, path, problems) {
  for (const field of schema.required ?? []) {
    if (!Object.hasOwn(value, field)) problems.push(`${path}.${field} is required.`);
  }

  for (const [field, entry] of Object.entries(value)) {
    const fieldSchema = schema.properties?.[field];
    if (fieldSchema === undefined) {
      if (schema.additionalProperties === false) problems.push(`${path}.${field} is not a field of this file.`);
      continue;
    }
    checkValue(fieldSchema, present(entry), `${path}.${field}`, problems);
  }
}

/** The structural half: the document's own rules, with the root reported as the file. */
function checkAgainstSchema(schema, value) {
  const problems = [];
  const candidate = present(value);
  if (!isPlainObject(candidate)) return ['the file must hold one JSON object.'];
  checkObject(schema, candidate, 'file', problems);
  return problems;
}

/** The rules the schema document cannot state: which field requires which other field. */
function resultFileRules(file, problems) {
  const failed = file.outcome === 'failed';
  if (failed && !Object.hasOwn(file, 'failureReason')) {
    problems.push('file.failureReason is required for a failed search.');
  }
  if (!failed && Object.hasOwn(file, 'failureReason')) {
    problems.push('file.failureReason belongs only with a failed search.');
  }

  const rows = Array.isArray(file.rows) ? file.rows : [];
  if ((file.outcome === 'no_matches' || failed) && rows.length > 0) {
    problems.push(`file.rows must be empty for a ${file.outcome} search.`);
  }

  const paging = file.paging;
  if (isPlainObject(paging) && paging.moreAvailable === true && paging.pagesRead >= paging.pageCap && file.outcome === 'complete') {
    problems.push('a search the page cap stopped must be reported as truncated, not complete.');
  }

  const seen = new Set();
  rows.forEach((row, index) => {
    if (!isPlainObject(row)) return;
    const path = `file.rows[${index}]`;
    if (typeof row.supplierProductId === 'string') {
      if (seen.has(row.supplierProductId)) {
        problems.push(`${path} reports supplier product id "${row.supplierProductId}", which another row already reports.`);
      }
      seen.add(row.supplierProductId);
    }
    if (Object.hasOwn(row, 'workItemId') && row.workItemId !== file.workItemId) {
      problems.push(`${path}.workItemId is not this file's work item.`);
    }
    if (Object.hasOwn(row, 'barcode') && !Object.hasOwn(row, 'barcodeScope')) {
      problems.push(`${path}.barcodeScope is required beside a barcode, because a unit and a carton code mean different packs.`);
    }
    if (Object.hasOwn(row, 'sizeValue') !== Object.hasOwn(row, 'sizeUnit')) {
      problems.push(`${path} must report a size with its unit, or neither.`);
    }
  });
}

/** The rule the schema document cannot state: exactly one proposal per match file. */
function matchFileRules(file, problems) {
  const names = ['suggestedBrand', 'suggestedName', 'suggestedSizeValue', 'suggestedSizeUnit'];
  const suggested = names.filter((field) => Object.hasOwn(file, field));
  const namesExisting = Object.hasOwn(file, 'masterProductId');

  if (namesExisting && suggested.length > 0) {
    problems.push('a match file proposes exactly one thing: an existing master product, or a complete new description.');
    return;
  }
  if (!namesExisting && suggested.length === 0) {
    problems.push('a match file proposes exactly one thing: an existing master product, or a complete new description.');
    return;
  }
  if (!namesExisting && suggested.length !== names.length) {
    problems.push(`a suggested new master product needs all of: ${names.join(', ')}.`);
  }
}

function answer(problems) {
  return problems.length === 0 ? { ok: true, problems: [] } : { ok: false, problems };
}

/** Validates one `price-collector-out/<work-item-id>.json`. Never throws on a malformed value. */
export function validateResultFile(value) {
  const problems = checkAgainstSchema(SCHEMA.resultFile, value);
  if (problems.length === 0) resultFileRules(present(value), problems);
  return answer(problems);
}

/** Validates one `price-collector-out/match-<waiting-key>.json`. Never throws. */
export function validateMatchFile(value) {
  const problems = checkAgainstSchema(SCHEMA.matchFile, value);
  if (problems.length === 0) matchFileRules(present(value), problems);
  return answer(problems);
}

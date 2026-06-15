#!/usr/bin/env node

/**
 * Tests for domain replacement logic in package-api-server.js
 *
 * Run with: node --test
 *
 * Domain replacement is source-domain matched: a `domain` is rewritten to the target
 * domain only when it equals the package's own (source) domain. Any `domain` on a
 * different domain is a genuine cross-domain reference and is preserved — at every level
 * (root, attributes, data[], subprocess `process`, nested objects). `crossDomain: true`
 * is exempt and the `config` subtree is skipped entirely.
 */

const { test } = require('node:test');
const assert = require('node:assert/strict');

const { replaceDomainInJson } = require('./package-api-server.js');

const SOURCE = 'local-test-domain';
const TARGET = 'real-domain';

test('Scenario 1: subprocess process.domain equal to source domain is rewritten to target', () => {
    const input = {
        key: 'my-subprocess-task',
        domain: SOURCE,
        process: { key: 'start-another-flow', domain: SOURCE, flow: 'sys-flows', version: '1.0.0' }
    };

    const result = replaceDomainInJson(input, TARGET, SOURCE);

    assert.equal(result.domain, TARGET, 'top-level domain replaced');
    assert.equal(result.process.domain, TARGET, 'same-domain subprocess ref replaced');
    assert.equal(result.process.flow, 'sys-flows');
    assert.equal(result.process.version, '1.0.0');
});

test('Scenario 2: subprocess process.domain on a different domain is preserved', () => {
    const input = {
        key: 'my-subprocess-task',
        domain: SOURCE,
        process: { key: 'sms-flow', domain: 'notification', flow: 'sys-flows', version: '1.0.0' }
    };

    const result = replaceDomainInJson(input, TARGET, SOURCE);

    assert.equal(result.domain, TARGET, 'top-level domain replaced');
    assert.equal(result.process.domain, 'notification', 'cross-domain subprocess ref preserved');
});

test('crossDomain:true is exempt at top level and inside process', () => {
    const input = {
        key: 'cross-task',
        domain: SOURCE,
        crossDomain: true,
        process: { key: 'ref', domain: SOURCE, crossDomain: true, flow: 'sys-flows' }
    };

    const result = replaceDomainInJson(input, TARGET, SOURCE);

    assert.equal(result.domain, SOURCE, 'crossDomain top-level domain preserved');
    assert.equal(result.process.domain, SOURCE, 'crossDomain process ref preserved even when same domain');
});

test('Only same-domain fields are replaced; differing domains are preserved everywhere', () => {
    const input = {
        key: 'task',
        domain: SOURCE,
        attributes: { domain: SOURCE },
        data: [
            { key: 'd1', domain: SOURCE },
            { key: 'd2', domain: 'some-other-domain' }
        ]
    };

    const result = replaceDomainInJson(input, TARGET, SOURCE);

    assert.equal(result.domain, TARGET);
    assert.equal(result.attributes.domain, TARGET, 'same-domain attributes replaced');
    assert.equal(result.data[0].domain, TARGET, 'same-domain data item replaced');
    assert.equal(result.data[1].domain, 'some-other-domain', 'cross-domain data item preserved');
});

test('Nested non-process object with a different domain is preserved', () => {
    const input = {
        domain: SOURCE,
        reference: { key: 'ext', domain: 'other-domain' },
        attributes: { nested: { domain: 'yet-another-domain' } }
    };

    const result = replaceDomainInJson(input, TARGET, SOURCE);

    assert.equal(result.domain, TARGET);
    assert.equal(result.reference.domain, 'other-domain', 'nested cross-domain reference preserved');
    assert.equal(result.attributes.nested.domain, 'yet-another-domain', 'deeply nested cross-domain preserved');
});

test('No regression: config subtree is left untouched', () => {
    const input = {
        key: 'task',
        domain: SOURCE,
        config: { domain: SOURCE, nested: { domain: SOURCE } }
    };

    const result = replaceDomainInJson(input, TARGET, SOURCE);

    assert.equal(result.domain, TARGET);
    assert.equal(result.config.domain, SOURCE, 'config domain untouched');
    assert.equal(result.config.nested.domain, SOURCE, 'nested config domain untouched');
});

test('Nested domain inside process follows the same same-domain-only rule', () => {
    const input = {
        domain: SOURCE,
        process: {
            domain: SOURCE,
            mapping: { domain: SOURCE },
            externalRef: { domain: 'notification' }
        }
    };

    const result = replaceDomainInJson(input, TARGET, SOURCE);

    assert.equal(result.process.domain, TARGET, 'process root same-domain replaced');
    assert.equal(result.process.mapping.domain, TARGET, 'nested same-domain replaced');
    assert.equal(result.process.externalRef.domain, 'notification', 'nested cross-domain preserved');
});

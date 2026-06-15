#!/usr/bin/env node

/**
 * Tests for domain replacement logic in package-api-server.js
 *
 * Run with: node --test
 *
 * Covers the subprocess (`process` block) domain replacement rules from issue #729:
 * - same-domain process refs are rewritten to the target domain
 * - cross-domain process refs are preserved
 * - crossDomain:true exemption, config skipping, and non-process replacement regressions
 */

const { test } = require('node:test');
const assert = require('node:assert/strict');

const { replaceDomainInJson, replaceProcessDomainInJson } = require('./package-api-server.js');

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

test('No regression: non-process domain fields are still replaced unconditionally', () => {
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
    assert.equal(result.attributes.domain, TARGET);
    assert.equal(result.data[0].domain, TARGET);
    assert.equal(result.data[1].domain, TARGET, 'non-process domains replaced regardless of original value');
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

test('replaceProcessDomainInJson honors config skip', () => {
    const input = { domain: SOURCE, config: { domain: SOURCE } };

    const result = replaceProcessDomainInJson(input, TARGET, SOURCE);

    assert.equal(result.domain, TARGET);
    assert.equal(result.config.domain, SOURCE, 'config untouched inside process subtree');
});

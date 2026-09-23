// @ts-check
//
// ESLint flat configuration for the Angular 19 frontend.
//
// Rule selection principle: catch real defects and dead code, not style. Formatting, quoting,
// import ordering and similar preferences are deliberately left out - they produce noise without
// finding bugs, and the repository has no Prettier setup for them to agree with.
//
// Run with `npm run lint` (which is `ng lint`); both validation scripts call it.

const eslint = require('@eslint/js');
const tseslint = require('typescript-eslint');
const angular = require('angular-eslint');

module.exports = tseslint.config(
  {
    // Build output, dependencies and generated CSS are never linted.
    ignores: [
      'dist/**',
      'node_modules/**',
      '.angular/**',
      'src/styles.css',
      'style.css',
      'eslint.config.js',
      'postcss.config.js',
      'postcss.config.mjs',
      'tailwind.config.js',
    ],
  },
  {
    files: ['**/*.ts'],
    extends: [
      eslint.configs.recommended,
      ...tseslint.configs.recommended,
      ...angular.configs.tsRecommended,
    ],
    processor: angular.processInlineTemplates,
    rules: {
      // ---- Angular component/directive conventions -------------------------------------
      '@angular-eslint/directive-selector': [
        'error',
        { type: 'attribute', prefix: 'app', style: 'camelCase' },
      ],
      '@angular-eslint/component-selector': [
        'error',
        { type: 'element', prefix: 'app', style: 'kebab-case' },
      ],

      // ---- Angular lifecycle / interface mistakes --------------------------------------
      // Declaring ngOnInit without implementing OnInit, misspelling a hook, calling a hook
      // by hand, or putting logic in the constructor that belongs in ngOnInit.
      '@angular-eslint/use-lifecycle-interface': 'error',
      '@angular-eslint/no-empty-lifecycle-method': 'error',
      '@angular-eslint/contextual-lifecycle': 'error',
      '@angular-eslint/no-conflicting-lifecycle': 'error',
      '@angular-eslint/no-pipe-impure': 'error',
      '@angular-eslint/no-output-on-prefix': 'error',
      '@angular-eslint/no-output-native': 'error',
      '@angular-eslint/prefer-output-readonly': 'error',

      // ---- Unused code -----------------------------------------------------------------
      // Base rule off, TypeScript-aware version on: the base rule misreports type-only usage.
      'no-unused-vars': 'off',
      '@typescript-eslint/no-unused-vars': [
        'error',
        {
          args: 'after-used',
          // A leading underscore is the opt-out for a deliberately unused binding.
          argsIgnorePattern: '^_',
          varsIgnorePattern: '^_',
          caughtErrorsIgnorePattern: '^_',
          destructuredArrayIgnorePattern: '^_',
        },
      ],

      // ---- Unsafe / unnecessary `any` --------------------------------------------------
      // `any` is reported but not fatal: the Nayax import and report payload mapping still
      // use it in places, and rewriting that typing is out of scope for this change. New code
      // shows the warning immediately, so the count moves in one direction only.
      '@typescript-eslint/no-explicit-any': 'warn',
      '@typescript-eslint/no-unnecessary-type-assertion': 'off', // needs type-aware linting
      '@typescript-eslint/no-non-null-asserted-optional-chain': 'error',
      '@typescript-eslint/no-wrapper-object-types': 'error',

      // ---- Equality and correctness ----------------------------------------------------
      // `==`/`!=` is a real defect source with the nullable financial values this app carries
      // (0 == '' and null == undefined both silently pass). `== null` stays allowed because it
      // is the idiomatic "null or undefined" check.
      eqeqeq: ['error', 'always', { null: 'ignore' }],
      'no-unreachable': 'error',
      'no-fallthrough': 'error',
      'no-constant-condition': 'error',
      'no-constant-binary-expression': 'error',
      'no-self-compare': 'error',
      'no-dupe-else-if': 'error',
      'no-duplicate-case': 'error',
      'no-unsafe-optional-chaining': 'error',
      'no-unmodified-loop-condition': 'error',
      'no-template-curly-in-string': 'error',
      'no-var': 'error',
      'prefer-const': 'error',
      // Leftover debug logging. console.error/console.warn are how the app already reports
      // failed requests, so only the debug-style calls are reported.
      'no-console': ['warn', { allow: ['error', 'warn'] }],
      'no-return-await': 'error',
      'require-atomic-updates': 'error',

      // ---- TypeScript correctness ------------------------------------------------------
      '@typescript-eslint/no-empty-object-type': 'error',
      '@typescript-eslint/no-misused-new': 'error',
      '@typescript-eslint/no-namespace': 'error',
      '@typescript-eslint/no-this-alias': 'error',
      '@typescript-eslint/no-unsafe-declaration-merging': 'error',
      '@typescript-eslint/no-unsafe-function-type': 'error',
      '@typescript-eslint/prefer-as-const': 'error',
      '@typescript-eslint/triple-slash-reference': 'error',
    },
  },
  {
    files: ['**/*.html'],
    extends: [
      ...angular.configs.templateRecommended,
      ...angular.configs.templateAccessibility,
    ],
    rules: {
      // ---- Angular template errors -----------------------------------------------------
      '@angular-eslint/template/no-negated-async': 'error',
      '@angular-eslint/template/banana-in-box': 'error',
      '@angular-eslint/template/eqeqeq': ['error', { allowNullOrUndefined: true }],
      '@angular-eslint/template/no-duplicate-attributes': 'error',
      '@angular-eslint/template/use-track-by-function': 'warn',

      // ---- Accessibility ---------------------------------------------------------------
      // AGENTS.md requires accessible labels and keyboard behaviour. The existing templates do
      // not fully satisfy the a11y rule set yet, so these report as warnings: they are visible
      // on every lint run and on every new template, without turning this change into an
      // accessibility remediation project.
      '@angular-eslint/template/click-events-have-key-events': 'warn',
      '@angular-eslint/template/interactive-supports-focus': 'warn',
      '@angular-eslint/template/label-has-associated-control': 'warn',
      '@angular-eslint/template/alt-text': 'warn',
      '@angular-eslint/template/elements-content': 'warn',
      '@angular-eslint/template/valid-aria': 'warn',
      '@angular-eslint/template/role-has-required-aria': 'warn',
      '@angular-eslint/template/table-scope': 'warn',
      '@angular-eslint/template/no-autofocus': 'warn',
      '@angular-eslint/template/no-distracting-elements': 'warn',
      '@angular-eslint/template/no-positive-tabindex': 'warn',
      '@angular-eslint/template/mouse-events-have-key-events': 'warn',
    },
  },
);

// Passkey (WebAuthn) client helper — TECHNICAL_SPECIFICATION §3.3.
//
// Adapted from the ASP.NET Core Blazor Web template's PasskeySubmit.razor.js, with two changes:
//   * it is a plain classic script (the account pages are static-SSR and load no JS modules), loaded
//     once from App.razor, guarded against double-definition; and
//   * the ceremony endpoints are this app's routes (AccountRoutes.PasskeyCreationOptions /
//     PasskeyRequestOptions) rather than the template's /Account/* paths — keep these two default
//     URLs in sync with ObligationsEnforcement.AccountRoutes. An element may override either with a
//     `creation-options-url` / `request-options-url` attribute; the first-administrator setup wizard
//     uses this to point attestation at its anonymous /setup/passkey/creation-options endpoint (§3.6).
//
// The <passkey-submit> element is form-associated: it intercepts the __passkeySubmit button, runs the
// browser ceremony, writes {Name}.CredentialJson (or {Name}.Error) into the form, and submits natively
// — which bypasses Blazor's EditForm validation so the passkey button never trips the password rules.
(function () {
    'use strict';

    if (customElements.get('passkey-submit')) {
        return;
    }

    const CREATION_OPTIONS_URL = '/account/passkey/creation-options';
    const REQUEST_OPTIONS_URL = '/account/passkey/request-options';

    const browserSupportsPasskeys =
        typeof navigator.credentials !== 'undefined' &&
        typeof window.PublicKeyCredential !== 'undefined' &&
        typeof window.PublicKeyCredential.parseCreationOptionsFromJSON === 'function' &&
        typeof window.PublicKeyCredential.parseRequestOptionsFromJSON === 'function';

    async function fetchWithErrorHandling(url, options = {}) {
        const response = await fetch(url, {
            credentials: 'include',
            ...options,
        });
        if (!response.ok) {
            const text = await response.text();
            console.error(text);
            throw new Error(`The server responded with status ${response.status}.`);
        }
        return response;
    }

    async function createCredential(url, headers, signal) {
        const optionsResponse = await fetchWithErrorHandling(url, {
            method: 'POST',
            headers,
            signal,
        });
        const optionsJson = await optionsResponse.json();
        const options = PublicKeyCredential.parseCreationOptionsFromJSON(optionsJson);
        return await navigator.credentials.create({ publicKey: options, signal });
    }

    async function requestCredential(url, username, mediation, headers, signal) {
        const query = encodeURIComponent(username ?? '');
        const optionsResponse = await fetchWithErrorHandling(`${url}?username=${query}`, {
            method: 'POST',
            headers,
            signal,
        });
        const optionsJson = await optionsResponse.json();
        const options = PublicKeyCredential.parseRequestOptionsFromJSON(optionsJson);
        return await navigator.credentials.get({ publicKey: options, mediation, signal });
    }

    customElements.define('passkey-submit', class extends HTMLElement {
        static formAssociated = true;

        connectedCallback() {
            this.internals = this.attachInternals();
            this.attrs = {
                operation: this.getAttribute('operation'),
                name: this.getAttribute('name'),
                emailName: this.getAttribute('email-name'),
                creationOptionsUrl: this.getAttribute('creation-options-url') || CREATION_OPTIONS_URL,
                requestOptionsUrl: this.getAttribute('request-options-url') || REQUEST_OPTIONS_URL,
                requestTokenName: this.getAttribute('request-token-name'),
                requestTokenValue: this.getAttribute('request-token-value'),
            };

            this.internals.form.addEventListener('submit', (event) => {
                if (event.submitter?.name === '__passkeySubmit') {
                    event.preventDefault();
                    this.obtainAndSubmitCredential();
                }
            });

            this.tryAutofillPasskey();
        }

        disconnectedCallback() {
            this.abortController?.abort();
        }

        async obtainCredential(useConditionalMediation, signal) {
            if (!browserSupportsPasskeys) {
                throw new Error('Some passkey features are missing. Please update your browser.');
            }

            const headers = {
                [this.attrs.requestTokenName]: this.attrs.requestTokenValue,
            };

            if (this.attrs.operation === 'Create') {
                return await createCredential(this.attrs.creationOptionsUrl, headers, signal);
            } else if (this.attrs.operation === 'Request') {
                const username = this.attrs.emailName
                    ? new FormData(this.internals.form).get(this.attrs.emailName)
                    : '';
                const mediation = useConditionalMediation ? 'conditional' : undefined;
                return await requestCredential(this.attrs.requestOptionsUrl, username, mediation, headers, signal);
            } else {
                throw new Error(`Unknown passkey operation '${this.attrs.operation}'.`);
            }
        }

        async obtainAndSubmitCredential(useConditionalMediation = false) {
            this.abortController?.abort();
            this.abortController = new AbortController();
            const signal = this.abortController.signal;
            const formData = new FormData();
            try {
                const credential = await this.obtainCredential(useConditionalMediation, signal);
                const credentialJson = JSON.stringify(credential);
                formData.append(`${this.attrs.name}.CredentialJson`, credentialJson);
            } catch (error) {
                if (error.name === 'AbortError') {
                    // The user explicitly canceled the operation — return without error.
                    return;
                }
                console.error(error);
                if (useConditionalMediation) {
                    // Conditional mediation is not user-initiated; log but do not surface an error.
                    return;
                }
                const errorMessage = error.name === 'NotAllowedError'
                    ? 'No passkey was provided by the authenticator.'
                    : error.message;
                formData.append(`${this.attrs.name}.Error`, errorMessage);
            }
            this.internals.setFormValue(formData);
            this.internals.form.submit();
        }

        async tryAutofillPasskey() {
            if (browserSupportsPasskeys &&
                this.attrs.operation === 'Request' &&
                await PublicKeyCredential.isConditionalMediationAvailable?.()) {
                await this.obtainAndSubmitCredential(/* useConditionalMediation */ true);
            }
        }
    });
})();

# Microsoft 365 authentication email

Verification emails use Microsoft Graph with an Entra app registration and application credentials. No SMTP server or mailbox password is used. Both Student sign-in codes and staff recovery codes use the same sender.

## App registration

In Microsoft Entra admin center, open your existing app registration:

1. Copy the Directory (tenant) ID and Application (client) ID from Overview.
2. Under API permissions, add **Microsoft Graph → Application permissions → Mail.Send** and grant administrator consent. Delegated Mail.Send alone does not support this unattended flow.
3. Under Certificates & secrets, create a client secret and copy its **Value**, not its Secret ID, into your local secret store.
4. Choose the existing Exchange Online mailbox whose user principal name will be configured as `Email:From`. The app sends through `/users/{mailbox}/sendMail`. Have the tenant administrator scope the app's mailbox access as appropriate for the institution.

## Local configuration

Run from `H:\AI\AcademicRepostory`, replacing the placeholders with your app details. Do not paste the client secret into chat or checked-in JSON.

```powershell
dotnet user-secrets set 'Email:DeliveryMode' 'MicrosoftGraph' --project AcademicRepository
dotnet user-secrets set 'Email:TenantId' '<directory-tenant-id>' --project AcademicRepository
dotnet user-secrets set 'Email:ClientId' '<application-client-id>' --project AcademicRepository
dotnet user-secrets set 'Email:ClientSecret' '<client-secret-value>' --project AcademicRepository
dotnet user-secrets set 'Email:From' 'noreply@smiu.edu.pk' --project AcademicRepository
```

For Production, provide the same values through secure deployment configuration using `Email__DeliveryMode`, `Email__TenantId`, `Email__ClientId`, `Email__ClientSecret`, and `Email__From`. The existing `AuthenticationCodes__HmacKey` requirement also applies. Restart the app after updating configuration. No new database migration is required for this delivery change.

Development defaults to `DevelopmentLog` until `Email:DeliveryMode` is overridden. To return to local logging, set `Email:DeliveryMode` to `DevelopmentLog` in user secrets. That mode is prohibited outside Development. Old Email:Host, Port, Username and Password settings are no longer used and can be removed.

## Verification and behavior

### Development Student test recipient

For local testing, Student OTP delivery can be redirected while the account still uses `CSC20F005@smiu.edu.pk`, StudentNumber `CSC-20F-005`, and its mapped Computer Science department:

```powershell
dotnet user-secrets set 'Email:DevelopmentStudentRecipient' 'ranjhanisaddam@smiu.edu.pk' --project AcademicRepository
```

This override is configured locally. Run in **Development** with `Email:DeliveryMode=MicrosoftGraph` and restart the app after changing configuration. It redirects all Student verification emails in that local instance, including first-time onboarding. Staff recovery recipients are unchanged. The code still verifies the originally entered Student account; access to the test mailbox therefore permits testing those Student accounts. No real Student mailbox ownership is established by redirected delivery. Startup and the sender reject an override outside Development, and the recipient must be an SMIU address. The setting is kept in user secrets, not committed configuration.

To restore normal Student delivery:

```powershell
dotnet user-secrets remove 'Email:DevelopmentStudentRecipient' --project AcademicRepository
```

Email examples use `CSC20F005@smiu.edu.pk`: CSC is the department, 20 the year, F/S the Fall/Spring session, and 005 the sequence. Stored Student Numbers use separators; email addresses do not.

Request a code for an existing active SMIU Student with a department, check the recipient mailbox and verify the code. Then exercise Forgot Password with a staff account. Existing resend/attempt/rate limits apply.

The service requests an app-only token for `https://graph.microsoft.com/.default`, then calls Graph's sendMail endpoint over HTTPS. It obtains a token for each send, uses a 30-second HTTP timeout per request, disables redirects and does not automatically retry a send. It does not save these messages to Sent Items. Graph's `202 Accepted` response means the request was accepted, not a guarantee of inbox delivery; Exchange message trace can diagnose delivery issues. Credentials, access tokens, mail bodies and remote error bodies are not logged by this implementation.

Missing/invalid configuration fails startup. Authentication or Graph errors are handled by the existing code service, which invalidates the affected OTP and returns the generic request response. For authentication errors, check tenant/client IDs, secret value and expiry. For permission errors, check application Mail.Send, admin consent and mailbox authorization.

Automated transport tests use simulated HTTP responses and never contact Microsoft 365. During the 2026-10-06 review, local Graph configuration was present and the user confirmed live delivery was already verified and requested skipping another send. The agent did not send a live email. Both Graph rejection and timeout regression tests confirm the affected OTP is invalidated.

References: [Microsoft Graph sendMail](https://learn.microsoft.com/en-us/graph/api/user-sendmail?view=graph-rest-1.0), [client credentials flow](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-client-creds-grant-flow).

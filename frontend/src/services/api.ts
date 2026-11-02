type HttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'

interface ApiRequestOptions {
    method?: HttpMethod
    body?: unknown
}

export type ValidationErrors = Record<string, string[]>

export class ApiError extends Error {
    constructor(
        public readonly status: number,
        message: string,
        public readonly validationErrors: ValidationErrors = {},
    ) {
        super(message)
        this.name = 'ApiError'
    }
}

function asRecord(value: unknown): Record<string, unknown> | null {
    if (value === null || typeof value !== 'object' || Array.isArray(value)) {
        return null
    }

    return value as Record<string, unknown>
}

async function sendRequest(
    url: string,
    options: RequestInit = {},
): Promise<Response> {
    try {
        return await fetch(url, {
            ...options,
            credentials: 'same-origin',
            cache: 'no-store',
        })
    } catch {
        throw new ApiError(0, '無法連線至伺服器，請檢查網路或稍後再試。')
    }
}

async function readResponse(response: Response): Promise<unknown> {
    if (response.status === 204) {
        return undefined
    }

    const contentType = response.headers.get('content-type') ?? ''

    if (!contentType.toLowerCase().includes('json')) {
        return undefined
    }

    try {
        return await response.json()
    } catch {
        return undefined
    }
}

function createApiError(status: number, data: unknown): ApiError {
    const payload = asRecord(data)

    const message =
        typeof payload?.message === 'string'
            ? payload.message
            : typeof payload?.title === 'string'
                ? payload.title
                : `請求失敗（HTTP ${status}）。`

    const validationErrors: ValidationErrors = {}
    const errors = asRecord(payload?.errors)

    if (errors) {
        for (const [field, value] of Object.entries(errors)) {
            if (Array.isArray(value)) {
                validationErrors[field] = value.filter(
                    (item): item is string => typeof item === 'string',
                )
            }
        }
    }

    return new ApiError(status, message, validationErrors)
}

async function getCsrfToken(): Promise<string> {
    const response = await sendRequest('/api/auth/csrf')
    const data = await readResponse(response)

    if (!response.ok) {
        throw createApiError(response.status, data)
    }

    const payload = asRecord(data)

    if (typeof payload?.csrfToken !== 'string' || !payload.csrfToken) {
        throw new ApiError(response.status, '無法取得 CSRF Token。')
    }

    return payload.csrfToken
}

export async function apiRequest<T>(
    path: string,
    options: ApiRequestOptions = {},
): Promise<T> {
    if (!path.startsWith('/api/')) {
        throw new Error('API 路徑必須以 /api/ 開頭。')
    }

    const method = options.method ?? 'GET'

    if (method === 'GET' && options.body !== undefined) {
        throw new Error('GET 請求不可包含 Body。')
    }

    const headers: Record<string, string> = {
        Accept: 'application/json',
    }

    if (method !== 'GET') {
        headers['X-CSRF-TOKEN'] = await getCsrfToken()
    }

    if (options.body !== undefined) {
        headers['Content-Type'] = 'application/json; charset=utf-8'
    }

    const response = await sendRequest(path, {
        method,
        headers,
        body:
            options.body === undefined
                ? undefined
                : JSON.stringify(options.body),
    })

    const data = await readResponse(response)

    if (!response.ok) {
        throw createApiError(response.status, data)
    }

    return data as T
}

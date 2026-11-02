<script setup lang="ts">
import { reactive, ref } from 'vue'
import { ApiError } from '@/services/api'
import { createPurchaseRequest } from '@/services/purchaseRequestService'

import type { CreatePurchaseRequestInput, CreatedPurchaseRequest } from '@/types/purchaseRequest'

const form = reactive<CreatePurchaseRequestInput>({
  title: '',
  purpose: '',
  items: [],
})

const saving = ref(false)
const errorMessage = ref('')
const validationMessages = ref<string[]>([])
const createdRequest = ref<CreatedPurchaseRequest | null>(null)

function addItem() {
  if (form.items.length >= 20) return

  form.items.push({
    name: '',
    quantity: 1,
    estimatedUnitPrice: 0,
  })
}

function removeItem(index: number) {
  form.items.splice(index, 1)
}

async function saveDraft() {
  if (saving.value || createdRequest.value) return

  saving.value = true
  errorMessage.value = ''
  validationMessages.value = []

  try {
    const result = await createPurchaseRequest({
      title: form.title.trim(),
      purpose: form.purpose?.trim() || null,
      items: form.items.map((item) => ({
        name: item.name.trim(),
        quantity: item.quantity,
        estimatedUnitPrice: item.estimatedUnitPrice,
      })),
    })

    createdRequest.value = result
  } catch (error) {
    if (error instanceof ApiError) {
      errorMessage.value = error.message

      validationMessages.value = Object.values(error.validationErrors).flat()
    } else {
      errorMessage.value = '儲存失敗，請稍後再試。'
    }
  } finally {
    saving.value = false
  }
}

function resetForm() {
  form.title = ''
  form.purpose = ''
  form.items = []

  errorMessage.value = ''
  validationMessages.value = []
  createdRequest.value = null
}
</script>

<template>
  <main class="form-page">
    <div class="form-container">
      <header class="form-header">
        <h1>新增採購申請</h1>
        <p>填寫申請資料並儲存為草稿，之後可以繼續編輯。</p>
      </header>

      <section v-if="createdRequest" class="success-panel" role="status">
        <h2>草稿建立成功</h2>

        <p>申請編號：{{ createdRequest.id }}</p>
        <p>申請標題：{{ createdRequest.title }}</p>
        <p>目前狀態：草稿</p>

        <button type="button" @click="resetForm">建立另一張草稿</button>
      </section>

      <form v-else @submit.prevent="saveDraft">
        <fieldset :disabled="saving">
          <section class="form-section">
            <h2>基本資料</h2>

            <div class="form-field">
              <label for="title">申請標題 *</label>

              <input
                id="title"
                v-model="form.title"
                type="text"
                maxlength="120"
                required
                placeholder="例如：辦公室設備採購"
              />
            </div>

            <div class="form-field">
              <label for="purpose">採購用途</label>

              <textarea
                id="purpose"
                v-model="form.purpose"
                maxlength="1000"
                rows="4"
                placeholder="說明採購用途（選填）"
              />
            </div>
          </section>

          <section class="form-section">
            <div class="section-heading">
              <h2>採購品項</h2>

              <button
                type="button"
                class="secondary-button"
                :disabled="form.items.length >= 20"
                @click="addItem"
              >
                新增品項
              </button>
            </div>

            <p class="hint">草稿可以沒有品項，最多新增 20 筆。</p>

            <p v-if="form.items.length === 0" class="empty-message">尚未新增採購品項。</p>

            <div v-for="(item, index) in form.items" :key="index" class="item-card">
              <div class="section-heading">
                <h3>品項 {{ index + 1 }}</h3>

                <button type="button" class="remove-button" @click="removeItem(index)">移除</button>
              </div>

              <div class="form-field">
                <label :for="`item-name-${index}`"> 品項名稱 * </label>

                <input
                  :id="`item-name-${index}`"
                  v-model="item.name"
                  type="text"
                  maxlength="150"
                  required
                  placeholder="例如：筆記型電腦"
                />
              </div>

              <div class="item-fields">
                <div class="form-field">
                  <label :for="`item-quantity-${index}`"> 數量 * </label>

                  <input
                    :id="`item-quantity-${index}`"
                    v-model.number="item.quantity"
                    type="number"
                    min="1"
                    max="999"
                    step="1"
                    required
                  />
                </div>

                <div class="form-field">
                  <label :for="`item-price-${index}`"> 預估單價（元）* </label>

                  <input
                    :id="`item-price-${index}`"
                    v-model.number="item.estimatedUnitPrice"
                    type="number"
                    min="0.01"
                    step="0.01"
                    required
                  />
                </div>
              </div>
            </div>
          </section>

          <div v-if="errorMessage" class="error-panel" role="alert">
            <p>{{ errorMessage }}</p>

            <ul v-if="validationMessages.length > 0">
              <li v-for="(message, index) in validationMessages" :key="index">
                {{ message }}
              </li>
            </ul>
          </div>

          <div class="form-actions">
            <button type="submit" :disabled="saving">
              {{ saving ? '儲存中...' : '儲存草稿' }}
            </button>
          </div>
        </fieldset>
      </form>
    </div>
  </main>
</template>

<style scoped>
.form-page {
  min-height: 100vh;
  padding: 40px 20px;
  background: #f3f5f9;
  color: #1f2937;
}

.form-container {
  max-width: 800px;
  margin: 0 auto;
}

.form-header {
  margin-bottom: 24px;
}

.form-header h1 {
  margin-bottom: 8px;
}

.form-header p,
.hint {
  color: #64748b;
}

.form-section,
.success-panel {
  margin-bottom: 24px;
  padding: 24px;
  border: 1px solid #e5e7eb;
  border-radius: 10px;
  background: #fff;
}

.form-section h2 {
  margin-top: 0;
}

.form-field {
  margin-bottom: 20px;
}

label {
  display: block;
  margin-bottom: 8px;
  font-weight: 600;
}

input,
textarea {
  box-sizing: border-box;
  width: 100%;
  padding: 12px;
  border: 1px solid #cbd5e1;
  border-radius: 6px;
  font: inherit;
}

input:focus-visible,
textarea:focus-visible {
  outline: 2px solid #2563eb;
  outline-offset: 2px;
}

fieldset {
  min-width: 0;
  margin: 0;
  padding: 0;
  border: 0;
}

.section-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}

.item-card {
  margin-top: 16px;
  padding: 20px;
  border: 1px solid #e2e8f0;
  border-radius: 8px;
}

.item-card h3 {
  margin: 0;
}

.item-fields {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
}

.empty-message {
  padding: 20px;
  border-radius: 6px;
  background: #f8fafc;
  color: #64748b;
  text-align: center;
}

button {
  padding: 10px 18px;
  border: 0;
  border-radius: 6px;
  background: #2563eb;
  color: #fff;
  font: inherit;
  cursor: pointer;
}

button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.secondary-button {
  background: #475569;
}

.remove-button {
  background: #b91c1c;
}

.form-actions {
  display: flex;
  justify-content: flex-end;
}

.error-panel {
  margin-bottom: 20px;
  padding: 16px;
  border-radius: 6px;
  background: #fef2f2;
  color: #b91c1c;
}

.success-panel {
  border-color: #86efac;
}

@media (max-width: 600px) {
  .item-fields {
    grid-template-columns: 1fr;
    gap: 0;
  }

  .section-heading {
    flex-wrap: wrap;
  }
}
</style>

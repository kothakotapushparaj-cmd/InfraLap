const WHATSAPP_NUMBER = "919392931016"; // replace with your real WhatsApp number
const CALL_NUMBER = "+919392931016";    // replace with your real call number

document.addEventListener("DOMContentLoaded", () => {
  // ===== Mobile menu toggle =====
  const menuToggle = document.getElementById("menuToggle");
  const mainNav = document.querySelector(".main-nav");

  if (menuToggle && mainNav) {
    menuToggle.addEventListener("click", () => {
      mainNav.classList.toggle("show");
    });
  }

  // ===== Product enquiry buttons =====
  const productButtons = document.querySelectorAll(".product-enquiry");

  productButtons.forEach((btn) => {
    btn.addEventListener("click", (e) => {
      e.preventDefault();
      const product = btn.getAttribute("data-product") || "Laptop Enquiry";
      const message = `Hello InfraLap Solutions, I want to enquire about this laptop:\n\n${product}\n\nPlease share price, availability, and details.`;
      const whatsappUrl = `https://wa.me/${WHATSAPP_NUMBER}?text=${encodeURIComponent(message)}`;
      window.open(whatsappUrl, "_blank");
    });
  });

  // ===== Contact form -> WhatsApp =====
  const contactForm = document.getElementById("contactForm");

  if (contactForm) {
    contactForm.addEventListener("submit", (e) => {
      e.preventDefault();

      const fullName = contactForm.querySelector('[name="fullName"]')?.value.trim() || "";
      const phone = contactForm.querySelector('[name="phone"]')?.value.trim() || "";
      const email = contactForm.querySelector('[name="email"]')?.value.trim() || "";
      const requirement = contactForm.querySelector('[name="requirement"]')?.value || "";
      const messageText = contactForm.querySelector('[name="message"]')?.value.trim() || "";

      if (!fullName || !phone || !requirement) {
        alert("Please fill Full Name, Phone Number, and Requirement Type.");
        return;
      }

      const message =
        `Hello InfraLap Solutions,

I want to send an enquiry.

Name: ${fullName}
Phone: ${phone}
Email: ${email}
Requirement: ${requirement}
Message: ${messageText}`;

      const whatsappUrl = `https://wa.me/${WHATSAPP_NUMBER}?text=${encodeURIComponent(message)}`;
      window.open(whatsappUrl, "_blank");
      contactForm.reset();
    });
  }

  // ===== Sticky call button click =====
  const callButton = document.querySelector(".floating-call");
  if (callButton) {
    callButton.setAttribute("href", `tel:${CALL_NUMBER}`);
  }
});